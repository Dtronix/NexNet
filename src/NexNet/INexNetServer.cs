using System.Collections.Concurrent;
using NexNet.Invocation;

namespace NexNet;

internal interface INexNetServer<TClientProxy>
    where TClientProxy : ProxyInvocationBase, IProxyInvoker, new()
{
    /// <summary>
    /// Cache for all the server hub contexts.
    /// </summary>
    ConcurrentBag<ServerHubContext<TClientProxy>> ServerHubContextCache { get; }
}
