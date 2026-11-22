using System.Collections.Concurrent;
using NexNet.Pipes;

#pragma warning disable VSTHRD200

namespace NexNet.TestNexus.Interop;

/// <summary>
/// C# client nexus of the interop tests. Each method calls its handler when one is set, and otherwise echoes its
/// argument (<see cref="ClientNotify"/> records the message in <see cref="Notifications"/>).
/// </summary>
[Nexus<IInteropClientNexus, IInteropServerNexus>(NexusType = NexusType.Client)]
public partial class InteropClientNexus
{
    /// <summary>Messages received by <see cref="ClientNotify"/>, in order.</summary>
    public ConcurrentQueue<string> Notifications { get; } = new();

    public Action<string>? ClientNotifyHandler { get; set; }
    public Func<ValueTask>? ClientPingHandler { get; set; }
    public Func<string?, ValueTask<string?>>? ClientEchoStringHandler { get; set; }
    public Func<InteropAllTypes?, ValueTask<InteropAllTypes?>>? ClientEchoAllTypesHandler { get; set; }
    public Func<InteropShape?, ValueTask<InteropShape?>>? ClientEchoShapeHandler { get; set; }
    public Func<int, int, CancellationToken, ValueTask<int>>? ClientDelayHandler { get; set; }
    public Func<INexusDuplexPipe, ValueTask>? ClientEchoPipeHandler { get; set; }

    public void ClientNotify(string message)
    {
        Notifications.Enqueue(message);
        ClientNotifyHandler?.Invoke(message);
    }

    public ValueTask ClientPing() => ClientPingHandler?.Invoke() ?? ValueTask.CompletedTask;

    public ValueTask<string?> ClientEchoString(string? value) => ClientEchoStringHandler?.Invoke(value) ?? new(value);

    public ValueTask<InteropAllTypes?> ClientEchoAllTypes(InteropAllTypes? value) => ClientEchoAllTypesHandler?.Invoke(value) ?? new(value);

    public ValueTask<InteropShape?> ClientEchoShape(InteropShape? value) => ClientEchoShapeHandler?.Invoke(value) ?? new(value);

    public async ValueTask<int> ClientDelay(int milliseconds, int value, CancellationToken cancellationToken)
    {
        if (ClientDelayHandler != null)
            return await ClientDelayHandler(milliseconds, value, cancellationToken).ConfigureAwait(false);

        await Task.Delay(milliseconds, cancellationToken).ConfigureAwait(false);
        return value;
    }

    public async ValueTask ClientEchoPipe(INexusDuplexPipe pipe)
    {
        if (ClientEchoPipeHandler != null)
        {
            await ClientEchoPipeHandler(pipe).ConfigureAwait(false);
            return;
        }

        while (true)
        {
            var result = await pipe.Input.ReadAsync().ConfigureAwait(false);
            var buffer = result.Buffer;
            foreach (var segment in buffer)
                await pipe.Output.WriteAsync(segment).ConfigureAwait(false);
            pipe.Input.AdvanceTo(buffer.End);
            if (result.IsCompleted || result.IsCanceled)
                break;
        }

        await pipe.CompleteAsync().ConfigureAwait(false);
    }
}
