using NexNet.Collections;

namespace NexNet.Invocation;

/// <summary>
/// Defines the configuration steps for setting up Nexus-backed collections
/// before starting synchronization.
/// </summary>
public interface IConfigureCollectionManager
{
    /// <summary>
    /// Registers or updates the synchronization mode for a list collection
    /// identified by the given <paramref name="id"/>.
    /// </summary>
    /// <typeparam name="T">The element type stored in the list.</typeparam>
    /// <param name="id">
    /// A unique identifier for the list collection to configure.  
    /// This must match the identifier used when retrieving or starting the collection.
    /// </param>
    /// <param name="mode">
    /// The <see cref="NexusCollectionMode"/> indicating how the list should synchronize 
    /// (e.g., one-way server-to-client or bi-directional).
    /// </param>
    void ConfigureList<T>(ushort id, NexusCollectionMode mode);

    /// <summary>
    /// Marks the end of all collection configuration calls.  
    /// After this method is invoked, no further calls to <see cref="ConfigureList{T}"/>
    /// may be made, and the configured collections are ready to be started.
    /// </summary>
    void CompleteConfigure();
}
