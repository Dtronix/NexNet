namespace NexNet.Internals;

/// <summary>
/// Result of <see cref="INexusSession.TryWriteInvocation{TMessage}"/>.
/// </summary>
internal enum InvocationWriteResult
{
    /// <summary>
    /// Committed to the session's output, in order. Not yet necessarily received by the peer.
    /// </summary>
    Written,

    /// <summary>
    /// Writing would have to wait: no invocation credit, another send in progress (or a backed-up transport), or
    /// arguments that must be streamed. Nothing was taken or written.
    /// </summary>
    Busy,

    /// <summary>
    /// The session is not connected.
    /// </summary>
    Failed,
}
