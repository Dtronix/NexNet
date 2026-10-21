using System;
using System.Collections.Generic;
using NexNet.RateLimiting;

namespace NexNet.Diagnostics;

/// <summary>
/// Live state of one session, read when <c>GetSnapshot()</c> was called. See docs/articles/metrics.md.
/// </summary>
/// <remarks>
/// The values are read one at a time from a running session, so they are not an atomic picture. A snapshot exposes
/// internal state, including session, pipe and invocation IDs: do not serve it to unauthenticated callers.
/// </remarks>
public sealed record NexusSessionSnapshot
{
    /// <summary>Session ID.</summary>
    public long Id { get; init; }

    /// <summary>True for a server session, false for a client.</summary>
    public bool IsServer { get; init; }

    /// <summary>Transport name, as in the <c>nexnet.transport</c> metric tag.</summary>
    public string Transport { get; init; } = "";

    /// <summary>Connection state.</summary>
    public ConnectionState State { get; init; }

    /// <summary>True once the handshake (and, on a server, authentication) completed.</summary>
    public bool IsEstablished { get; init; }

    /// <summary>Time since the transport connection was established.</summary>
    public TimeSpan Connected { get; init; }

    /// <summary>Time since a complete frame was last received.</summary>
    public TimeSpan SinceLastReceived { get; init; }

    /// <summary>Time since data was last written to the transport; null if nothing was.</summary>
    public TimeSpan? SinceLastSent { get; init; }

    /// <summary>How long the outbound transport flush in progress has been running; null if none is.</summary>
    public TimeSpan? FlushInProgress { get; init; }

    /// <summary>Receive memory of the session.</summary>
    public NexusBudgetSnapshot Budget { get; init; } = null!;

    /// <summary>Invocations in both directions.</summary>
    public NexusInvocationSnapshot Invocations { get; init; } = null!;

    /// <summary>Pipes holding a receive window: open, or closing until both sides confirmed.</summary>
    public IReadOnlyList<NexusPipeSnapshot> Pipes { get; init; } = [];

    /// <summary>Arguments and results larger than one frame being received or sent.</summary>
    public IReadOnlyList<NexusLargeMessageSnapshot> LargeMessages { get; init; } = [];

    /// <summary>Frames written to the transport.</summary>
    public long FramesSent { get; init; }

    /// <summary>Bytes written to the transport.</summary>
    public long BytesSent { get; init; }

    /// <summary>Frames read from the transport.</summary>
    public long FramesReceived { get; init; }

    /// <summary>Bytes read from the transport.</summary>
    public long BytesReceived { get; init; }
}

/// <summary>
/// A session's receive memory (<see cref="NexNet.Transports.ConfigBase.SessionBudget"/>).
/// </summary>
public sealed record NexusBudgetSnapshot
{
    /// <summary>Budget in total, in bytes.</summary>
    public long Limit { get; init; }

    /// <summary>Bytes reserved: pipe windows, the invocation queue and large values.</summary>
    public long Reserved { get; init; }
}

/// <summary>
/// A session's invocations.
/// </summary>
public sealed record NexusInvocationSnapshot
{
    /// <summary>Invocations received and waiting for an execution slot.</summary>
    public int Queued { get; init; }

    /// <summary>Invocations running.</summary>
    public int Executing { get; init; }

    /// <summary>Most invocations that run at once (<see cref="NexNet.Transports.ConfigBase.MaxConcurrentConnectionInvocations"/>).</summary>
    public int MaxExecuting { get; init; }

    /// <summary>Invocations the peer may still queue on this side.</summary>
    public int InboundCredits { get; init; }

    /// <summary>Invocations this side may still send before the peer returns credit.</summary>
    public int OutboundCredits { get; init; }

    /// <summary>Calls waiting for the peer to return credit.</summary>
    public int CreditWaits { get; init; }

    /// <summary>Calls this side made that are waiting for their result.</summary>
    public int AwaitingResults { get; init; }

    /// <summary>Invocations skipped because the peer returned no credit in time.</summary>
    public long CreditDrops { get; init; }
}

/// <summary>
/// One pipe.
/// </summary>
public sealed record NexusPipeSnapshot
{
    /// <summary>Pipe ID; until the peer acknowledges a pipe this side opened, only this side's half is set.</summary>
    public ushort Id { get; init; }

    /// <summary>True once both sides set the pipe up.</summary>
    public bool IsReady { get; init; }

    /// <summary>True once this side's reader completed.</summary>
    public bool ReaderCompleted { get; init; }

    /// <summary>True once this side's writer completed.</summary>
    public bool WriterCompleted { get; init; }

    /// <summary>How long ago this side sent its Complete without the peer confirming; null if not closing.</summary>
    public TimeSpan? Closing { get; init; }

    /// <summary>Receive window, in bytes.</summary>
    public int Window { get; init; }

    /// <summary>Bytes the peer may still send.</summary>
    public long ReceiveCredit { get; init; }

    /// <summary>Bytes received and not yet read by the application.</summary>
    public long Buffered { get; init; }

    /// <summary>Bytes read and not yet granted back to the peer.</summary>
    public long ConsumedNotGranted { get; init; }

    /// <summary>Extra credit granted for an item larger than the window, not yet consumed.</summary>
    public long ExtraOutstanding { get; init; }

    /// <summary>Time since data last arrived; null if none has.</summary>
    public TimeSpan? SinceLastData { get; init; }

    /// <summary>A channel item that has partly arrived; null if there is none.</summary>
    public NexusPartialItemSnapshot? PartialItem { get; init; }

    /// <summary>Bytes this side may still send.</summary>
    public long SendCredit { get; init; }

    /// <summary>How long the writer has waited for send credit; null if it is not waiting.</summary>
    public TimeSpan? WaitingForCredit { get; init; }

    /// <summary>Bytes the writer still has to send while it waits for credit; 0 if it is not waiting.</summary>
    public long WaitingBytes { get; init; }
}

/// <summary>
/// A channel item that has partly arrived.
/// </summary>
public sealed record NexusPartialItemSnapshot
{
    /// <summary>Length of the item, length prefix included.</summary>
    public long Length { get; init; }

    /// <summary>Bytes of it received so far.</summary>
    public long Received { get; init; }
}

/// <summary>
/// Whether data flows to this side or from it.
/// </summary>
public enum NexusDirection
{
    /// <summary>The peer is sending to this side.</summary>
    Inbound,

    /// <summary>This side is sending to the peer.</summary>
    Outbound,
}

/// <summary>
/// What a large message carries.
/// </summary>
public enum NexusLargeMessageKind
{
    /// <summary>Invocation arguments.</summary>
    Argument,

    /// <summary>An invocation result.</summary>
    Result,
}

/// <summary>
/// An argument or result larger than one frame being transferred.
/// </summary>
public sealed record NexusLargeMessageSnapshot
{
    /// <summary>Whether it is being received or sent.</summary>
    public NexusDirection Direction { get; init; }

    /// <summary>Argument or result.</summary>
    public NexusLargeMessageKind Kind { get; init; }

    /// <summary>ID of the invocation it belongs to.</summary>
    public ushort InvocationId { get; init; }

    /// <summary>Total length, in bytes.</summary>
    public long Length { get; init; }

    /// <summary>Bytes transferred so far.</summary>
    public long Transferred { get; init; }

    /// <summary>Time since the last chunk.</summary>
    public TimeSpan SinceLastProgress { get; init; }

    /// <summary>True for an inbound value this side refused, whose remaining chunks are being discarded.</summary>
    public bool Aborting { get; init; }
}

/// <summary>
/// Live state of a server, read when <c>GetSnapshot()</c> was called.
/// </summary>
/// <remarks>
/// Exposes internal state: do not serve it to unauthenticated callers.
/// </remarks>
public sealed record NexusServerSnapshot
{
    /// <summary>Server state.</summary>
    public NexusServerState State { get; init; }

    /// <summary>Connections that have not completed authentication.</summary>
    public int PendingHandshakes { get; init; }

    /// <summary>Rate limiter statistics; null when rate limiting is off.</summary>
    public ConnectionRateLimitStats? RateLimiter { get; init; }

    /// <summary>Authenticated sessions.</summary>
    public IReadOnlyList<NexusSessionSnapshot> Sessions { get; init; } = [];
}
