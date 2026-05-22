using System.IO.Pipelines;
using System.Threading.Tasks;
using NexNet.Transports;

namespace NexNet.Testing.Transports.InProcess;

/// <summary>
/// In-process implementation of <see cref="ITransport"/> that exposes a pair of
/// <see cref="System.IO.Pipelines.Pipe"/> ends as the duplex stream. Used in pairs: the server
/// side and client side of a single connection share two pipes cross-wired so that one side's
/// output is the other side's input.
/// </summary>
internal sealed class InProcessTransport : ITransport
{
    private readonly PipeReader _input;
    private readonly PipeWriter _output;
    private bool _closed;

    public PipeReader Input => _input;
    public PipeWriter Output => _output;
    public string? RemoteAddress { get; }
    public int? RemotePort => null;

    public InProcessTransport(PipeReader input, PipeWriter output, string remoteAddress)
    {
        _input = input;
        _output = output;
        RemoteAddress = remoteAddress;
    }

    public ValueTask CloseAsync(bool linger)
    {
        if (_closed)
            return ValueTask.CompletedTask;
        _closed = true;

        // Completing the writer signals to the peer's reader that no more data will arrive.
        // Completing the reader signals to the peer's writer that no further reads will occur.
        // The `linger` flag is a no-op for in-process: there is no socket buffer to drain.
        try { _output.Complete(); } catch { /* already completed */ }
        try { _input.Complete(); } catch { /* already completed */ }
        return ValueTask.CompletedTask;
    }
}
