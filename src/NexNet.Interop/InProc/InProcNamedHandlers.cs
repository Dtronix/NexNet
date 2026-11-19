using System.Reflection;
using System.Text.Json.Nodes;
using NexNet.Interop.Scripting;
using NexNet.Pipes;

namespace NexNet.Interop.InProc;

/// <summary>
/// The built-in handlers of <see cref="NamedStep"/> for the C# client: <c>pipe.echo</c> (write back every byte until the
/// server completes, then complete), <c>pipe.drain</c> (read until the server completes, then complete),
/// <c>pipe.readExact</c> (option <c>length</c>: read that many bytes, then complete) and <c>channel.echo</c> (write back
/// every item until the server completes, then complete).
/// </summary>
internal static class InProcNamedHandlers
{
    private static readonly MethodInfo ChannelEchoMethod =
        typeof(InProcNamedHandlers).GetMethod(nameof(ChannelEchoAsync), BindingFlags.NonPublic | BindingFlags.Static)!;

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
                var channel = call.Pipe ?? throw Missing(step);
                var itemType = channel.GetType().GetInterfaces()
                    .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(INexusDuplexChannel<>))
                    ?.GetGenericArguments()[0] ?? throw Missing(step);
                await ((ValueTask)ChannelEchoMethod.MakeGenericMethod(itemType).Invoke(null, [channel])!).ConfigureAwait(false);
                return null;
            default:
                throw new NotSupportedException(
                    $"The C# client has no built-in handler '{step.Handler}' (pipe.echo, pipe.drain, pipe.readExact, channel.echo).");
        }
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
}
