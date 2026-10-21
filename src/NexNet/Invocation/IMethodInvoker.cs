using System.Threading;
using System.Threading.Tasks;
using NexNet.Messages;
using NexNet.Pipes;
using NexNet.Serialization;

namespace NexNet.Invocation;

/// <summary>
/// Interface to be used by sessions to invoke method on local nexus.
/// </summary>
public interface IMethodInvoker
{
    internal ValueTask InvokeMethod(InvocationMessage message);
    internal void CancelInvocation(InvocationCancellationMessage message);

    /// <summary>
    /// Starts tracking a received invocation that expects a result, before it is queued, so that a cancellation that
    /// arrives before its method starts still cancels it.
    /// </summary>
    internal void TrackInvocation(InvocationMessage message);

    /// <summary>
    /// Gets the cancellation token source for an invocation whose method takes a cancellation token. An invocation
    /// that expects a result gets its own source, registered under its invocation ID so the caller can cancel it. A
    /// fire-and-forget invocation gets the session's shared source, canceled when the session ends: its invocation ID
    /// need not be unique, and nothing else can cancel it.
    /// </summary>
    /// <param name="message">The invocation.</param>
    /// <returns>Cancellation token source for the invocation.</returns>
    CancellationTokenSource RegisterCancellationToken(IInvocationMessage message);

    /// <summary>
    /// Releases the source returned by <see cref="RegisterCancellationToken"/> once the invocation has completed.
    /// </summary>
    /// <param name="message">The invocation.</param>
    /// <param name="cancellationTokenSource">The source returned for it by <see cref="RegisterCancellationToken"/>.</param>
    void ReturnCancellationToken(IInvocationMessage message, CancellationTokenSource cancellationTokenSource);
    
    /// <summary>
    /// Registers a duplex pipe for usage associates it with the specified invocation.
    /// </summary>
    /// <param name="startId">Pipe ID sent by the invoking side.</param>
    /// <param name="message">The invocation that carries the pipe. Its execution slot is freed once the pipe is
    /// registered, so a pipe that stays open does not hold one of the session's invocation slots.</param>
    /// <returns>Registered pipe</returns>
    ValueTask<INexusDuplexPipe> RegisterDuplexPipe(byte startId, IInvocationMessage message);

    /// <summary>
    /// Returns a the pipe associated with the specified invocation.
    /// </summary>
    ValueTask ReturnDuplexPipe(INexusDuplexPipe pipe);

    /// <summary>
    /// Options used to deserialize invocation arguments received from the remote peer.
    /// </summary>
    NexusSerializerOptions SerializerOptions { get; }
}
