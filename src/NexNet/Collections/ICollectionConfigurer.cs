using NexNet.Invocation;

namespace NexNet.Collections;

public interface ICollectionConfigurer
{
    /// <summary>
    /// Configures collections (if any) on the 
    /// </summary>
    /// <param name="manager"></param>
    static abstract void ConfigureCollections(IConfigureCollectionManager manager);
}
