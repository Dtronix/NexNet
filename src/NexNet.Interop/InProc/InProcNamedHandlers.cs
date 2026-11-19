using System.Reflection;
using System.Text.Json.Nodes;
using NexNet.Interop.Scripting;
using NexNet.Pipes;

namespace NexNet.Interop.InProc;

/// <summary>
/// The built-in handlers of <see cref="NamedStep"/> for the C# client: <c>pipe.echo</c> (write back every byte until the
/// server completes, then complete), <c>pipe.drain</c> (read until the server completes, then complete),
/// <c>pipe.readExact</c> (option <c>length</c>: read that many bytes, then complete), <c>channel.echo</c> (write back
/// every item until the server completes, then complete) and <c>channel.pump</c> (options <c>count</c>, <c>size</c>,
/// <c>delayMs</c>: write <c>count</c> items, then complete; control protocol §6.9).
/// </summary>
internal static class InProcNamedHandlers
{
    private static readonly MethodInfo ChannelEchoMethod =
        typeof(InProcNamedHandlers).GetMethod(nameof(ChannelEchoAsync), BindingFlags.NonPublic | BindingFlags.Static)!;

    private static readonly MethodInfo ChannelPumpMethod =
        typeof(InProcNamedHandlers).GetMethod(nameof(ChannelPumpAsync), BindingFlags.NonPublic | BindingFlags.Static)!;

    public static async ValueTask<JsonNode?> RunAsync(NamedStep step, ScriptCall call)
    {
        switch (step.Handler)
        {
            case "pipe.echo":
                await PipeEchoAsync(RequirePipe(step, call)).ConfigureAwait(false);
                return null;
            case "pipe.drain":
                await PipeReadAsync(RequirePipe(step, call), long.MaxValue).ConfigureAwait(false);
                return null;
            case "pipe.readExact":
                var length = step.Options?["length"]?.GetValue<long>()
                             ?? throw new ArgumentException("pipe.readExact needs the option 'length'.");
                await PipeReadAsync(RequirePipe(step, call), length).ConfigureAwait(false);
                return null;
            case "channel.echo":
            {
                var (channel, itemType) = RequireChannel(step, call);
                await ((ValueTask)ChannelEchoMethod.MakeGenericMethod(itemType).Invoke(null, [channel])!).ConfigureAwait(false);
                return null;
            }
            case "channel.pump":
            {
                var count = Option(step, "count") ?? throw new ArgumentException("channel.pump needs the option 'count'.");
                var size = Option(step, "size") ?? 0;
                var delayMs = Option(step, "delayMs") ?? 0;
                var (channel, itemType) = RequireChannel(step, call);
                if (!CanPump(itemType))
                {
                    throw new NotSupportedException(
                        $"channel.pump cannot make items of type {itemType} (integer types, string and byte[] only).");
                }

                var pump = (ValueTask)ChannelPumpMethod.MakeGenericMethod(itemType)
                    .Invoke(null, [channel, count, size, delayMs, call.CancellationToken])!;
                await pump.ConfigureAwait(false);
                return null;
            }
            default:
                throw new NotSupportedException(
                    $"The C# client has no built-in handler '{step.Handler}' (pipe.echo, pipe.drain, pipe.readExact, channel.echo, channel.pump).");
        }
    }

    /// <summary>
    /// Item <paramref name="index"/> of <c>channel.pump</c> for an item type of <paramref name="itemType"/>: an integer
    /// type gets the index wrapped to its width, <see cref="string"/> <paramref name="size"/> letters starting at
    /// <c>'a' + index mod 26</c>, and <c>byte[]</c> the first <paramref name="size"/> bytes of the pipe pattern of seed
    /// <paramref name="index"/>.
    /// </summary>
    /// <exception cref="NotSupportedException">Any other item type.</exception>
    internal static object PumpItem(Type itemType, int index, int size)
    {
        long i = index;
        return Type.GetTypeCode(itemType) switch
        {
            TypeCode.SByte => unchecked((sbyte)i),
            TypeCode.Byte => unchecked((byte)i),
            TypeCode.Int16 => unchecked((short)i),
            TypeCode.UInt16 => unchecked((ushort)i),
            TypeCode.Int32 => unchecked((int)i),
            TypeCode.UInt32 => unchecked((uint)i),
            TypeCode.Int64 => i,
            TypeCode.UInt64 => unchecked((ulong)i),
            TypeCode.String => string.Create(size, index, static (span, seed) =>
            {
                for (var j = 0; j < span.Length; j++)
                    span[j] = (char)('a' + (int)(((long)seed + j) % 26));
            }),
            _ when itemType == typeof(byte[]) => PipePattern.Create(size, index),
            _ => throw new NotSupportedException(
                $"channel.pump cannot make items of type {itemType} (integer types, string and byte[] only)."),
        };
    }

    private static bool CanPump(Type itemType)
        => itemType == typeof(byte[])
           || (!itemType.IsEnum && Type.GetTypeCode(itemType) is TypeCode.SByte or TypeCode.Byte or TypeCode.Int16
               or TypeCode.UInt16 or TypeCode.Int32 or TypeCode.UInt32 or TypeCode.Int64 or TypeCode.UInt64 or TypeCode.String);

    private static int? Option(NamedStep step, string name)
    {
        var node = step.Options?[name];
        if (node == null)
            return null;

        int value;
        try
        {
            value = node.GetValue<int>();
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException)
        {
            throw new ArgumentException($"{step.Handler}: the option '{name}' is not an integer.", e);
        }

        return value >= 0 ? value : throw new ArgumentException($"{step.Handler}: the option '{name}' is negative ({value}).");
    }

    private static (object Channel, Type ItemType) RequireChannel(NamedStep step, ScriptCall call)
    {
        var channel = call.Pipe ?? throw Missing(step);
        var itemType = channel.GetType().GetInterfaces()
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(INexusDuplexChannel<>))
            ?.GetGenericArguments()[0] ?? throw Missing(step);
        return (channel, itemType);
    }

    private static INexusDuplexPipe RequirePipe(NamedStep step, ScriptCall call)
        => call.Pipe as INexusDuplexPipe ?? throw Missing(step);

    private static InvalidOperationException Missing(NamedStep step)
        => new($"The handler '{step.Handler}' needs a method that takes a pipe or channel.");

    private static async ValueTask PipeEchoAsync(INexusDuplexPipe pipe)
    {
        while (true)
        {
            var result = await pipe.Input.ReadAsync().ConfigureAwait(false);
            foreach (var segment in result.Buffer)
                await pipe.Output.WriteAsync(segment).ConfigureAwait(false);
            pipe.Input.AdvanceTo(result.Buffer.End);
            if (result.IsCompleted || result.IsCanceled)
                break;
        }

        await pipe.CompleteAsync().ConfigureAwait(false);
    }

    private static async ValueTask PipeReadAsync(INexusDuplexPipe pipe, long length)
    {
        long read = 0;
        while (read < length)
        {
            var result = await pipe.Input.ReadAsync().ConfigureAwait(false);
            var buffer = result.Buffer.Slice(0, Math.Min(result.Buffer.Length, length - read));
            read += buffer.Length;
            pipe.Input.AdvanceTo(buffer.End);
            if (result.IsCompleted || result.IsCanceled)
                break;
        }

        await pipe.CompleteAsync().ConfigureAwait(false);
    }

    private static async ValueTask ChannelEchoAsync<T>(INexusDuplexChannel<T> channel)
    {
        var reader = await channel.GetReaderAsync().ConfigureAwait(false);
        var writer = await channel.GetWriterAsync().ConfigureAwait(false);
        await foreach (var item in reader.ConfigureAwait(false))
            await writer.WriteAsync(item).ConfigureAwait(false);
        await writer.CompleteAsync().ConfigureAwait(false);
    }

    private static async ValueTask ChannelPumpAsync<T>(INexusDuplexChannel<T> channel, int count, int size, int delayMs, CancellationToken cancellationToken)
    {
        var writer = await channel.GetWriterAsync().ConfigureAwait(false);
        for (var i = 0; i < count; i++)
        {
            if (!await writer.WriteAsync((T)PumpItem(typeof(T), i, size), cancellationToken).ConfigureAwait(false))
                break; // The server completed the channel; there is nobody left to write to.
            if (delayMs > 0 && i < count - 1)
                await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
        }

        await writer.CompleteAsync().ConfigureAwait(false);
    }
}
