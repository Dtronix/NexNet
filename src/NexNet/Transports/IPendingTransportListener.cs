using System.Threading;
using System.Threading.Tasks;

namespace NexNet.Transports;

/// <summary>
/// A listener whose accepted connections need a per-connection setup step before they are usable (the TLS handshake
/// of a TLS listener, the stream accept of a QUIC listener). The server accepts only the raw connection on its accept
/// loop and runs that step on its own task, so one slow or stalled peer never blocks other accepts.
/// </summary>
internal interface IPendingTransportListener : ITransportListener
{
    /// <summary>
    /// Accepts the next raw connection without running its setup step. Returns null when the accept was canceled or
    /// a single connection failed before it was accepted; the listener keeps accepting. Errors that make the
    /// listener itself unusable are thrown.
    /// </summary>
    ValueTask<IPendingTransport?> AcceptPendingAsync(CancellationToken cancellationToken);
}

/// <summary>
/// A connection accepted by an <see cref="IPendingTransportListener"/> whose setup step has not run yet.
/// Exactly one of <see cref="CompleteAsync"/> or <see cref="Abort"/> is called.
/// </summary>
internal interface IPendingTransport
{
    /// <summary>
    /// The remote address, known before the setup step. Same value as <see cref="ITransport.RemoteAddress"/> of the
    /// completed transport.
    /// </summary>
    string? RemoteAddress { get; }

    /// <summary>
    /// Runs the setup step, bounded by the transport's own handshake timeout and by
    /// <paramref name="cancellationToken"/>. Returns the usable transport, or null when the step failed or timed out;
    /// the failure is logged and the connection is closed. Never throws.
    /// </summary>
    ValueTask<ITransport?> CompleteAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Closes the connection at once without running the setup step (it was rejected). Never throws.
    /// </summary>
    void Abort();
}
