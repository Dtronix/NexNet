using System.Buffers;
using System.Threading.Tasks;
using NexNet.Messages;

namespace NexNet.Diagnostics;

/// <summary>
/// Opt-in observer of the raw bytes the sessions of one configuration read from and write to their transport. Set it
/// on <see cref="NexNet.Transports.ConfigBase.Tap"/>; with none set, sessions read and write the transport directly.
/// </summary>
/// <remarks>
/// The bytes are the NexNet stream itself: after TLS decryption and below any WebSocket or HTTP framing, starting with
/// the protocol preamble, then the greetings, then every frame, on every transport. Only the session factory sits on
/// the configuration: what is kept, dropped or redacted is the tap's business.
/// </remarks>
public interface INexusTap
{
    /// <summary>
    /// Called once per session as it is created, before it reads or writes a byte (the preamble included). Returns
    /// the observer for that session, or null to leave the session untapped: an untapped session never reaches the
    /// tap again.
    /// </summary>
    /// <remarks>
    /// Called on the thread creating the session; keep it short. An exception is logged and leaves the session
    /// untapped.
    /// </remarks>
    /// <param name="session">Identity of the new session.</param>
    INexusSessionTap? SessionStarted(in NexusTapSessionInfo session);
}

/// <summary>
/// Observer of one session's transport bytes, returned by <see cref="INexusTap.SessionStarted"/>. It stays installed for
/// the life of the session, so attaching and detaching viewers at runtime is done inside it.
/// </summary>
/// <remarks>
/// <para>
/// The data passed to <see cref="OnRead"/> and <see cref="OnWrite"/> is borrowed from the transport's buffers: it is
/// valid until the call returns or, when the call returns an incomplete <see cref="ValueTask"/>, until that task
/// completes. Copy what must outlive it.
/// </para>
/// <para>
/// Reads are reported from the session's read loop and writes under its write lock, so each direction is reported
/// in order and never concurrently with itself; the two directions may run concurrently with each other.
/// The session waits for an incomplete task before it processes the read or flushes the write: that is how a
/// lossless tap slows the connection down. A tap that must never slow the connection returns a completed task and
/// drops what it cannot keep. An exception (thrown or from a faulted task) is logged and stops the tapping of that
/// direction for the rest of the session.
/// </para>
/// </remarks>
public interface INexusSessionTap
{
    /// <summary>
    /// Bytes the session has just read from the transport: only the bytes that arrived since the previous report,
    /// never the unconsumed remainder of an earlier read again.
    /// </summary>
    /// <param name="data">The new bytes; borrowed (see the remarks of <see cref="INexusSessionTap"/>).</param>
    /// <param name="timestamp">When the read completed, in <see cref="System.Diagnostics.Stopwatch"/> ticks.</param>
    /// <returns>Completed to let the session process the read now; incomplete to hold the read loop until it completes.</returns>
    ValueTask OnRead(ReadOnlySequence<byte> data, long timestamp);

    /// <summary>
    /// Bytes the session is writing to the transport: everything committed since the previous flush, reported once,
    /// just before the flush. One call is one transport write.
    /// </summary>
    /// <param name="data">The committed bytes; borrowed (see the remarks of <see cref="INexusSessionTap"/>).</param>
    /// <param name="timestamp">When the flush started, in <see cref="System.Diagnostics.Stopwatch"/> ticks.</param>
    /// <returns>Completed to flush now; incomplete to hold the flush (and the session's writes) until it completes.</returns>
    ValueTask OnWrite(ReadOnlySequence<byte> data, long timestamp);

    /// <summary>
    /// The handshake completed. <paramref name="sessionId"/> is the session's final ID: a client session learns it from
    /// the server's greeting, so it differs from the ID it started with.
    /// </summary>
    /// <param name="sessionId">The session's ID.</param>
    void OnConnected(long sessionId);

    /// <summary>
    /// The session ended. Called once: after the disconnect completed the output, or when the read loop ended without
    /// a disconnect. A read that was already in progress can still be reported concurrently with it.
    /// </summary>
    /// <param name="reason">Why the session ended; <see cref="DisconnectReason.None"/> when it ended before any
    /// disconnect started.</param>
    void OnClosed(DisconnectReason reason);
}

/// <summary>
/// Identity of a session passed to <see cref="INexusTap.SessionStarted"/>.
/// </summary>
/// <param name="Id">Session ID. A server session's is final; a client session's is 0 until
/// <see cref="INexusSessionTap.OnConnected"/> reports the ID the server assigned.</param>
/// <param name="IsServer">True for a server session, false for a client.</param>
/// <param name="TransportName">Transport name, as in the <c>nexnet.transport</c> metric tag.</param>
/// <param name="Endpoint">Server only: name of the endpoint the session was accepted on; null for a client.</param>
/// <param name="RemoteAddress">Remote address reported by the transport, if any.</param>
/// <param name="RemotePort">Remote port reported by the transport, if any.</param>
/// <param name="Timestamp">When the session was created, in <see cref="System.Diagnostics.Stopwatch"/> ticks.</param>
public readonly record struct NexusTapSessionInfo(
    long Id,
    bool IsServer,
    string TransportName,
    string? Endpoint,
    string? RemoteAddress,
    int? RemotePort,
    long Timestamp);
