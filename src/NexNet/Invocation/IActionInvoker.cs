using System.Threading.Tasks;
using NexNet.Internals;
using NexNet.Messages;

namespace NexNet.Invocation;

public interface IMethodInvoker<TProxy>
    where TProxy : ProxyInvocationBase, IProxyInvoker, new()
{
    internal ValueTask InvokeMethod(InvocationRequestMessage message);
    internal void CancelInvocation(InvocationCancellationRequestMessage message);
}


public interface IProxyInvoker
{
    internal void Configure(INexNetSession session, ProxyInvocationMode mode, object? modeArguments);
}
