using System;
using System.Threading.Tasks;
using NexNet.Internals;
using NexNet.Invocation;
using NexNet.Pipes;

namespace NexNet.Collections;

internal interface INexusCollectionConnector
{
    /// <summary>
    /// Server only
    /// </summary>
    /// <param name="pipe"></param>
    /// <param name="context"></param>
    /// <returns></returns>
    public ValueTask ServerStartCollectionConnection(INexusDuplexPipe pipe, INexusSession context);
    
    /// <summary>
    /// Client Only
    /// </summary>
    /// <param name="invoker"></param>
    /// <param name="session"></param>
    void TryConfigureProxyCollection(IProxyInvoker invoker, INexusSession session);
}
