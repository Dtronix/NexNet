using NexNet.Invocation;

namespace NexNet.Collections;

/// <summary>
/// Allows for configuration of synchronized collections.
/// </summary>
public interface ICollectionConfigurer
{
    /// <summary>
    /// Configures collections (if any) on the 
    /// </summary>
    /// <param name="manager"></param>
    static abstract void ConfigureCollections(IConfigureCollectionManager manager);
}
