using System.Threading.Tasks;
using NexNet.Internals;

namespace NexNet.Pipes.Broadcast;

internal interface INexusBroadcastConnector
{
    /// <summary>
    /// Server only
    /// </summary>
    /// <param name="pipe"></param>
    /// <param name="context"></param>
    /// <returns></returns>
    public ValueTask ServerStartCollectionConnection(INexusDuplexPipe pipe, INexusSession context);
    
    void Start();
    
    void Stop();
}
