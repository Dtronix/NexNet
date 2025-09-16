using System;
using System.Threading.Tasks;
using NexNet.Collections;

namespace NexNet;

/// <summary>
/// Provides access to Nexus collections from a client connection.
/// </summary>
public interface INexusCollectionClientConnector : IDisposable
{
    /// <summary>
    /// Gets the collection associated with this client connection.
    /// </summary>
    /// <returns>A task that represents the asynchronous operation. The task result contains the Nexus collection.</returns>
    public ValueTask<INexusCollection> GetCollection();
}
