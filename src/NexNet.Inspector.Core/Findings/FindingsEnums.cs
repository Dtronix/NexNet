namespace NexNet.Inspector.Core.Findings;

/// <summary>How much a <see cref="Finding"/> matters; findings rank by it, most severe first.</summary>
public enum FindingSeverity : byte
{
    /// <summary>Worth knowing: an outlier or a condition the capture explains (attached mid-stream, cut off by its end).</summary>
    Info = 0,

    /// <summary>Probably a problem: a stall, a slow call, a lost result, data the capture dropped.</summary>
    Warning = 1,

    /// <summary>A protocol violation or failure: malformed frames, a result nothing asked for, a protocol-error disconnect.</summary>
    Error = 2,
}

/// <summary>What a <see cref="Finding"/> is about; each kind has one detector and one short code (<see cref="FindingsText.Code"/>).</summary>
public enum FindingKind : byte
{
    /// <summary>`protocol`: a frame that does not frame or decode, or a handshake that never completed.</summary>
    ProtocolError = 0,

    /// <summary>`disconnect`: the session ended for a reason other than a graceful close or a shutdown.</summary>
    Disconnect = 1,

    /// <summary>`schema`: the recorded schema differs from the workspace's, does not read, or the peers' nexuses mismatched.</summary>
    SchemaMismatch = 2,

    /// <summary>`gap`: ops the capture dropped (Drop overflow): what was in flight in that direction is incomplete.</summary>
    Gap = 3,

    /// <summary>`orphan`: a result (or large-message chunks) whose request was not captured.</summary>
    OrphanResult = 4,

    /// <summary>`cancel-race`: the caller canceled, but the callee's result (ok or exception) won the race.</summary>
    CancelRace = 5,

    /// <summary>`pending`: invocations that had no result when the session (or the capture) ended.</summary>
    PendingAtEnd = 6,

    /// <summary>`pipe-open`: pipes that were never completed by both sides when the session (or the capture) ended.</summary>
    PipeOpenAtEnd = 7,

    /// <summary>`window-stall`: a pipe writer used its whole credit window and waited for a window update.</summary>
    WindowStall = 8,

    /// <summary>`credit-stall`: a caller used all the invocation credits its peer allows and waited for credit back.</summary>
    CreditStall = 9,

    /// <summary>`slow`: a call over the slow threshold, or far above its method's usual (p99) duration.</summary>
    SlowCall = 10,

    /// <summary>`ping-rtt`: a ping whose answer took far longer than the session's usual round trip.</summary>
    PingRtt = 11,

    /// <summary>`large`: a large argument, result or channel item; near the receiver's limit or aborted.</summary>
    LargeMessage = 12,

    /// <summary>`chunk-storm`: very many large-message chunks in a short time.</summary>
    ChunkStorm = 13,

    /// <summary>`reconnect-loop`: one client reconnecting again and again (several sessions of a file).</summary>
    ReconnectLoop = 14,
}
