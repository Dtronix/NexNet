using NexNet.Pipes;

#pragma warning disable VSTHRD200

namespace NexNet.TestNexus.Interop;

/// <summary>
/// Client side of the interop nexus: the methods the server calls back. The cross-language harness scripts their
/// behavior per test; <see cref="InteropClientNexus"/> echoes by default.
/// </summary>
public partial interface IInteropClientNexus
{
    void ClientNotify(string message);
    ValueTask ClientPing();
    ValueTask<string?> ClientEchoString(string? value);
    ValueTask<InteropAllTypes?> ClientEchoAllTypes(InteropAllTypes? value);
    ValueTask<InteropShape?> ClientEchoShape(InteropShape? value);
    ValueTask<int> ClientDelay(int milliseconds, int value, CancellationToken cancellationToken);
    ValueTask ClientEchoPipe(INexusDuplexPipe pipe);
}
