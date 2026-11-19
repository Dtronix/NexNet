using System.Security.Cryptography;
using System.Text.Json.Nodes;
using NexNet.TestNexus.Interop;

namespace NexNet.Interop.Tests.Fake;

/// <summary>A pipe of the fake host, and the channel view of it (control protocol §6.7, §6.8).</summary>
internal sealed class FakePipe(string handle, FakeClient client, IRemotePipe pipe) : IAsyncDisposable
{
    private interface IChannel
    {
        Task<bool> WriteAsync(JsonArray items);

        Task CompleteAsync();

        Task<JsonObject> ReadAsync(int maximum);

        Task<JsonArray> ReadAllAsync();
    }

    private IChannel? _writer;
    private IChannel? _reader;
    private int _disposed;

    public string Handle { get; } = handle;

    public FakeClient Client { get; } = client;

    public IRemotePipe Pipe { get; } = pipe;

    public async Task<JsonObject> DispatchAsync(string op, JsonObject args)
    {
        switch (op)
        {
            case "pipe.ready":
                await Pipe.WhenReadyAsync();
                return new JsonObject();
            case "pipe.whenComplete":
                await Pipe.WhenCompletedAsync();
                return new JsonObject();
            case "pipe.write":
                if (args["pattern"] is JsonObject pattern)
                    await Pipe.WritePatternAsync(pattern["seed"]!.GetValue<int>(), pattern["length"]!.GetValue<long>());
                else
                    await Pipe.WriteAsync(Convert.FromBase64String(args["b64"]!.GetValue<string>()));
                return new JsonObject();
            case "pipe.read":
                var data = await Pipe.ReadAsync(args["min"]!.GetValue<int>(), args["max"]!.GetValue<int>());
                return new JsonObject { ["b64"] = Convert.ToBase64String(data.Span) };
            case "pipe.drain":
                var digest = await Pipe.DrainAsync();
                return new JsonObject { ["length"] = digest.Length, ["sha256"] = digest.Sha256 };
            case "pipe.complete":
                await Pipe.CompleteAsync();
                return new JsonObject();
            case "pipe.dispose":
                await DisposeAsync();
                return new JsonObject();
            case "channel.writer":
                _writer = await CreateAsync(args["type"]!.GetValue<string>(), writer: true);
                return new JsonObject();
            case "channel.reader":
                _reader = await CreateAsync(args["type"]!.GetValue<string>(), writer: false);
                return new JsonObject();
            case "channel.write":
                return new JsonObject { ["ok"] = await Writer.WriteAsync((JsonArray)args["items"]!.DeepClone()) };
            case "channel.complete":
                await Writer.CompleteAsync();
                return new JsonObject();
            case "channel.read":
                return await Reader.ReadAsync(args["max"]!.GetValue<int>());
            case "channel.readAll":
                return new JsonObject { ["items"] = await Reader.ReadAllAsync() };
            default:
                throw new ArgumentException("Unknown op '" + op + "'.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        Client.Scope.RemovePipe(this);
        await Pipe.DisposeAsync();
    }

    private IChannel Writer => _writer ?? throw new InvalidOperationException("channel.writer was not called on " + Handle + ".");

    private IChannel Reader => _reader ?? throw new InvalidOperationException("channel.reader was not called on " + Handle + ".");

    private async Task<IChannel> CreateAsync(string type, bool writer)
        => type switch
        {
            "long" => await Channel<long>.CreateAsync(Pipe, writer),
            "int" => await Channel<int>.CreateAsync(Pipe, writer),
            "string" => await Channel<string>.CreateAsync(Pipe, writer),
            "NexNet.TestNexus.Interop.InteropItem" => await Channel<InteropItem>.CreateAsync(Pipe, writer),
            _ => throw new NotSupportedException("The fake host has no channel of " + type + "."),
        };

    private sealed class Channel<T> : IChannel
    {
        private readonly TypedCodec<T> _codec = TypedCodecs.Get<T>();
        private IRemoteChannelWriter<T>? _writer;
        private IRemoteChannelReader<T>? _reader;

        public static async Task<IChannel> CreateAsync(IRemotePipe pipe, bool writer)
        {
            var channel = new Channel<T>();
            if (writer)
                channel._writer = await pipe.GetChannelWriterAsync<T>();
            else
                channel._reader = await pipe.GetChannelReaderAsync<T>();
            return channel;
        }

        public async Task<bool> WriteAsync(JsonArray items) => await _writer!.WriteManyAsync(items.Select(i => _codec.Decode(i)!).ToArray());

        public async Task CompleteAsync() => await _writer!.CompleteAsync();

        public async Task<JsonObject> ReadAsync(int maximum)
        {
            var batch = await _reader!.ReadBatchAsync(maximum);
            return new JsonObject { ["items"] = Encode(batch), ["complete"] = batch.Count == 0 };
        }

        public async Task<JsonArray> ReadAllAsync() => Encode(await _reader!.ReadAllAsync());

        private JsonArray Encode(IReadOnlyList<T> items) => new(items.Select(i => _codec.Encode(i)).ToArray());
    }

    internal static string Sha256(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));
}
