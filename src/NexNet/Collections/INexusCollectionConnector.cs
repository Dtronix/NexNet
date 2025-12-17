using System;
using System.Threading.Tasks;
using NexNet.Internals;
using NexNet.Invocation;
using NexNet.Pipes;

namespace NexNet.Collections;

internal interface INexusCollectionConnector
{
    /// <summary>
    /// Client Only
    /// </summary>
    /// <param name="invoker"></param>
    /// <param name="session"></param>
    void TryConfigureProxyCollection(IProxyInvoker invoker, INexusSession session);
}
