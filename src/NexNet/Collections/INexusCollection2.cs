using System.Collections;
using System.Threading;
using System.Threading.Tasks;
using NexNet.Collections.Lists;

namespace NexNet.Collections;

/// <summary>
/// Represents a synchronized collection providing methods to connect to and disconnect from the underlying data source;
/// </summary>
public interface INexusCollection2 : IEnumerable
{    
    /// <summary>
    /// Gets the current state of the collection’s connection to the server.
    /// </summary>
    /// <value>
    /// A <see cref="NexusCollectionState"/> enum indicating the state.
    /// </value>
    public NexusCollectionState State { get; }
    
    /// <summary>
    /// Event raised when the collection has been changed by the server.
    /// </summary>
    public ISubscriptionEvent<NexusCollectionChangedEventArgs> Changed { get; }
}
