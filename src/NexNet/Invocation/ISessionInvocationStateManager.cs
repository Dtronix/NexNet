using System;
using System.Threading;
using System.Threading.Tasks;
using NexNet.Internals;
using NexNet.Messages;

namespace NexNet.Invocation;

/// <summary>
/// Interface for managing invocation state for a session.
/// </summary>
internal interface ISessionInvocationStateManager
{
    /// <summary>
    /// Generates a unique invocation ID for the current session.
    /// </summary>
    /// <remarks>
    /// This method is thread-safe and ensures that the returned ID is not currently in use by any other invocation in the same session.
    /// </remarks>
    /// <param name="addToCurrentInvocations">If true, the invocation ID will be added to the current invocations list. If false, it will not.</param>
    /// <returns>A unique ushort value representing the invocation ID.</returns>
    ushort GetNextId(bool addToCurrentInvocations);

    /// <summary>
    /// Updates the invocation state with the result message.
    /// </summary>
    /// <param name="message">The result message to process.</param>
    void UpdateInvocationResult(InvocationResultMessage message);

    /// <summary>
    /// True if a call with this ID is waiting for its result.
    /// </summary>
    bool IsOutstanding(ushort invocationId);

    /// <summary>
    /// Calls waiting for their result. For snapshots: may take internal locks.
    /// </summary>
    int OutstandingCount => 0;

    /// <summary>
    /// Fails a call that is waiting for its result, e.g. because the peer refused its large argument.
    /// </summary>
    void FailInvocation(ushort invocationId, Exception exception);

    /// <summary>
    /// Frees a tracked ID taken with <c>GetNextId(true)</c> for an invocation that expects no result.
    /// </summary>
    void ReleaseId(ushort invocationId);

    /// <summary>
    /// Invokes a method and waits for the result.
    /// </summary>
    /// <param name="methodId">The method ID to invoke.</param>
    /// <param name="serializedArguments">Pre-serialized argument bytes.</param>
    /// <param name="session">The session to invoke on.</param>
    /// <param name="cancellationToken">Optional cancellation token.</param>
    /// <param name="argumentsOwner">Optional pooled buffer backing the arguments; returned once the message is sent.</param>
    /// <returns>The registered invocation state, or null if cancelled.</returns>
    ValueTask<RegisteredInvocationState?> InvokeMethodWithResultCore(
        ushort methodId,
        Memory<byte> serializedArguments,
        INexusSession session,
        CancellationToken? cancellationToken = null,
        NexNet.Serialization.PooledArrayBufferWriter? argumentsOwner = null);

    /// <summary>
    /// Cancels all pending invocations.
    /// </summary>
    void CancelAll();
}
