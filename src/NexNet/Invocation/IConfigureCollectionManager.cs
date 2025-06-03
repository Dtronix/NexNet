using NexNet.Collections;

namespace NexNet.Invocation;

public interface IConfigureCollectionManager
{
    void ConfigureList<T>(ushort id, NexusCollectionMode mode);
    
    void CompleteConfigure();
}
