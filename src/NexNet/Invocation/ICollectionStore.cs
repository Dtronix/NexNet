using System.Threading.Tasks;
using NexNet.Collections.Lists;
using NexNet.Pipes;

namespace NexNet.Invocation;

/// <summary>
/// Manages the creation and activation of Nexus-backed collections.
/// </summary>
public interface ICollectionStore
{
    /// <summary>
    /// Retrieves a previously configured synchronized list instance
    /// associated with the given collection identifier.
    /// </summary>
    /// <typeparam name="T">The element type stored in the list.</typeparam>
    /// <param name="id">
    /// A unique identifier for the collection.  
    /// Must match the same identifier used by the peer when starting the collection.
    /// </param>
    /// <returns>
    /// An <see cref="INexusList{T}"/> that represents the collection with the specified id.
    /// </returns>
    INexusList<T> GetList<T>(ushort id);

    /// <summary>
    /// Begins or resumes synchronization of the collection identified by <paramref name="id"/>
    /// over the provided duplex communication pipe.
    /// </summary>
    /// <param name="id">
    /// The unique identifier of the collection to start.  
    /// This must match the identifier used when the client or server requested the collection.
    /// </param>
    /// <param name="pipe">
    /// The <see cref="INexusDuplexPipe"/> used for message exchange provide collection updates.
    /// </param>
    /// <returns>
    /// A <see cref="ValueTask"/> that completes once the collection session has been
    /// successfully started and is ready to send and receive updates.
    /// </returns>
    ValueTask StartCollection(ushort id, INexusDuplexPipe pipe);
}
