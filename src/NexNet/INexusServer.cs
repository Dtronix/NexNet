using System.Collections.Concurrent;
using NexNet.Invocation;

namespace NexNet;

internal interface INexusServer<TClientProxy>
    where TClientProxy : ProxyInvocationBase, IProxyInvoker, new()
{
    /// <summary>
    /// Cache for all the server nexus contexts.
    /// </summary>
    ConcurrentBag<ServerNexusContext<TClientProxy>> ServerNexusContextCache { get; }
}
