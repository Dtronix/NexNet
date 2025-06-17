using System.Collections;
using System.Threading;
using System.Threading.Tasks;
using NexNet.Collections.Lists;

namespace NexNet.Collections;

/// <summary>
/// Represents a synchronized collection providing methods to connect to and disconnect from the underlying data source;
/// </summary>
public interface INexusCollection : IEnumerable
{    
    /// <summary>
    /// Gets the current state of the collection’s connection to the server.
    /// </summary>
    /// <value>
    /// A <see cref="NexusCollectionState"/> enum indicating the state.
    /// </value>
    public NexusCollectionState State { get; }
    
    /// <summary>
    /// Establishes a connection to the server backing this collection.
    /// </summary>
    /// <param name="token">
    /// A <see cref="CancellationToken"/> that can be used to cancel the connection attempt.
    /// </param>
    /// <returns>
    /// A <see cref="Task{TResult}"/> that completes with <c>true</c> if the connection
    /// was successfully established and accepted by the server; otherwise <c>false</c>.
    /// </returns>
    public Task<bool> ConnectAsync(CancellationToken token = default);
    
    /// <summary>
    /// Gracefully disconnects from the server
    /// </summary>
    /// <returns>
    /// A <see cref="Task"/> that completes once disconnection is finished.
    /// </returns>
    public Task DisconnectAsync();
    
    /// <summary>
    /// Task that completes upon client disconnection from the server.
    /// Is only valid after the ConnectAsync method completes successfully.
    /// On the server, this task will is not used and will always be complete.
    /// </summary>
    public Task DisconnectedTask { get; }
    
    /// <summary>
    /// Event raised when the collection has been changed by the server.
    /// </summary>
    public ISubscriptionEvent<NexusCollectionChangedEventArgs> Changed { get; }
}
