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
    /// Registers a cancellation token with an invocation id.
    /// </summary>
    /// <param name="invocationId">Invocation id to associate with this cancellation token.</param>
    /// <returns>Cancellation token source associated with a specific invocation.</returns>
    CancellationTokenSource RegisterCancellationToken(int invocationId);

    /// <summary>
    /// Returns a cancellation token for the specified invocation id.
    /// </summary>
    /// <param name="invocationId">invocation if to return the cancellation token for.</param>
    void ReturnCancellationToken(int invocationId);
    
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
