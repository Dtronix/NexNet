using System.Threading;
using System.Threading.Tasks;
using NexNet.Messages;
using NexNet.Pipes;

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
    /// <returns>Registered pipe</returns>
    ValueTask<INexusDuplexPipe> RegisterDuplexPipe(byte startId);

    /// <summary>
    /// Returns a the pipe associated with the specified invocation.
    /// </summary>
    ValueTask ReturnDuplexPipe(INexusDuplexPipe pipe);
}
