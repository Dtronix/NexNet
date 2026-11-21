using System.Text.Json.Nodes;

namespace NexNet.Interop.Remote;

/// <summary>A duplex pipe in a language host (control protocol §6.7, §6.8).</summary>
internal sealed class RemotePipe(RemoteClientDriver driver, string handle) : IRemotePipe
{
    private int _disposed;

    public string Handle { get; } = handle;

    private RemoteClientDriver Driver => driver;

    public async ValueTask WhenReadyAsync() => await Call("pipe.ready", null, RemoteClientDriver.DefaultWaitMs).ConfigureAwait(false);

    public async ValueTask WhenCompletedAsync() => await Call("pipe.whenComplete", null, RemoteClientDriver.DefaultWaitMs).ConfigureAwait(false);

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> data)
        => await Call("pipe.write", new JsonObject { ["b64"] = Convert.ToBase64String(data.Span) }, RemoteClientDriver.DefaultWaitMs).ConfigureAwait(false);

    public async ValueTask<bool> TryWriteAsync(ReadOnlyMemory<byte> data)
    {
        var result = await Call("pipe.tryWrite", new JsonObject { ["b64"] = Convert.ToBase64String(data.Span) }, RemoteClientDriver.DefaultWaitMs)
            .ConfigureAwait(false);
        return result["ok"]?.GetValue<bool>() == true;
    }

    public async ValueTask WritePatternAsync(int seed, long length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        var pattern = new JsonObject { ["seed"] = seed, ["length"] = length };
        await Call("pipe.write", new JsonObject { ["pattern"] = pattern }, RemoteClientDriver.DefaultWaitMs).ConfigureAwait(false);
    }

    public async ValueTask<ReadOnlyMemory<byte>> ReadAsync(int minimum, int maximum)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(minimum);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximum, Math.Max(1, minimum));
        var result = await Call("pipe.read", new JsonObject { ["min"] = minimum, ["max"] = maximum }, RemoteClientDriver.DefaultWaitMs).ConfigureAwait(false);
        return Convert.FromBase64String(result["b64"]?.GetValue<string>() ?? "");
    }

    public async ValueTask<PipeDigest> DrainAsync()
    {
        var result = await Call("pipe.drain", null, RemoteClientDriver.DefaultWaitMs).ConfigureAwait(false);
        return new PipeDigest(result["length"]!.GetValue<long>(), result["sha256"]!.GetValue<string>());
    }

    public async ValueTask CompleteAsync() => await Call("pipe.complete", null, RemoteClientDriver.DefaultOpMs).ConfigureAwait(false);

    public async ValueTask CompleteInputAsync() => await Call("pipe.completeInput", null, RemoteClientDriver.DefaultOpMs).ConfigureAwait(false);

    public async ValueTask<IRemoteChannelWriter<T>> GetChannelWriterAsync<T>()
    {
        var codec = TypedCodecs.Get<T>();
        await Call("channel.writer", new JsonObject { ["type"] = ControlJson.TypeExpression(codec.Schema) }, RemoteClientDriver.DefaultWaitMs)
            .ConfigureAwait(false);
        return new Writer<T>(this, codec);
    }

    public async ValueTask<IRemoteChannelReader<T>> GetChannelReaderAsync<T>()
    {
        var codec = TypedCodecs.Get<T>();
        await Call("channel.reader", new JsonObject { ["type"] = ControlJson.TypeExpression(codec.Schema) }, RemoteClientDriver.DefaultWaitMs)
            .ConfigureAwait(false);
        return new Reader<T>(this, codec);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0 || driver.Connection.IsClosed || !driver.Connection.HasScope(driver.Test))
            return;

        try
        {
            await Call("pipe.dispose", null, RemoteClientDriver.DefaultWaitMs).ConfigureAwait(false);
        }
        catch (Exception e) when (e is HostCrashedException or ObjectDisposedException)
        {
            // The host is gone, and the pipe with it.
        }
    }

    private Task<JsonObject> Call(string op, JsonObject? args, int timeoutMs)
    {
        args ??= new JsonObject();
        args["pipe"] = Handle;
        return driver.Connection.RequestAsync(driver.Test, op, args, timeoutMs);
    }

    private sealed class Writer<T>(RemotePipe pipe, TypedCodec<T> codec) : IRemoteChannelWriter<T>
    {
        public ValueTask<bool> WriteAsync(T item) => WriteManyAsync([item]);

        public async ValueTask<bool> WriteManyAsync(IEnumerable<T> items)
        {
            ArgumentNullException.ThrowIfNull(items);
            var array = new JsonArray(items.Select(i => codec.Encode(i)).ToArray());
            var result = await pipe.Call("channel.write", new JsonObject { ["items"] = array }, RemoteClientDriver.DefaultWaitMs).ConfigureAwait(false);
            return result["ok"]?.GetValue<bool>() == true;
        }

        public async ValueTask CompleteAsync()
            => await pipe.Call("channel.complete", null, RemoteClientDriver.DefaultOpMs).ConfigureAwait(false);
    }

    private sealed class Reader<T>(RemotePipe pipe, TypedCodec<T> codec) : IRemoteChannelReader<T>
    {
        public ValueTask<IReadOnlyList<T>> ReadBatchAsync(int maximum) => ReadBatchAsync(maximum, RemoteCancel.None);

        public async ValueTask<IReadOnlyList<T>> ReadBatchAsync(int maximum, RemoteCancel cancel)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximum);
            ArgumentNullException.ThrowIfNull(cancel);
            var args = new JsonObject { ["max"] = maximum };
            if (RemoteInvoker.Cancel(pipe.Driver, cancel, out var token) is { } json)
                args["cancel"] = json;
            try
            {
                var result = await pipe.Call("channel.read", args, RemoteClientDriver.DefaultWaitMs).ConfigureAwait(false);
                return Decode(result);
            }
            finally
            {
                token?.Dispose();
            }
        }

        public async ValueTask<IReadOnlyList<T>> ReadAllAsync()
        {
            var result = await pipe.Call("channel.readAll", null, RemoteClientDriver.DefaultWaitMs).ConfigureAwait(false);
            return Decode(result);
        }

        private T[] Decode(JsonObject result)
            => (result["items"] as JsonArray ?? new JsonArray()).Select(n => codec.Decode(n)!).ToArray();
    }
}
