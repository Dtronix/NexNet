using System.Threading;
using System.Threading.Tasks;
using NexNet.Messages;

namespace NexNet.Invocation;

/// <summary>
/// Interface to be used by sessions to invoke method on local hubs.
/// </summary>
/// <typeparam name="TProxy"></typeparam>
public interface IMethodInvoker<TProxy>
    where TProxy : ProxyInvocationBase, IProxyInvoker, new()
{
    internal ValueTask InvokeMethod(InvocationRequestMessage message);
    internal void CancelInvocation(InvocationCancellationRequestMessage message);

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
}
