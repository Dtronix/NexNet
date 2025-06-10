using System.Collections;
using System.Threading;
using System.Threading.Tasks;

namespace NexNet.Collections;

public interface INexusCollection : IEnumerable
{
    public Task<bool> ConnectAsync(CancellationToken token = default);
    public Task  DisconnectAsync();
    
    public NexusCollectionState State { get; }
}
