using System.Collections;
using System.Threading.Tasks;

namespace NexNet.Collections;

public interface INexusCollection : IEnumerable
{
    public Task ConnectAsync();
    public Task DisconnectAsync();
}
