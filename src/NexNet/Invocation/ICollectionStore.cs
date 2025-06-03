using System.Threading.Tasks;
using NexNet.Collections.Lists;
using NexNet.Pipes;

namespace NexNet.Invocation;

public interface ICollectionStore
{
    INexusList<T> GetList<T>(ushort id);
    ValueTask StartCollection(ushort id, INexusDuplexPipe pipe);
}
