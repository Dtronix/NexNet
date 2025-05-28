using System.Threading.Tasks;

namespace NexNet.Collections;

public interface INexusCollection
{
    public Task ConnectAsync();
    public Task DisconnectAsync();
}
