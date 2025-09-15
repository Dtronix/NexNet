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
    /// <param name="collectionConnector"></param>
    /// <returns>
    /// A <see cref="Task{TResult}"/> that completes with <c>true</c> if the connection
    /// was successfully established and accepted by the server; otherwise <c>false</c>.
    /// </returns>
    public void ConfigureRelay(INexusCollectionClientConnector collectionConnector);
    
    /// <summary>
    /// Establishes a connection to a parent collection, making this collection a relay
    /// that forwards any changes from the parent collection to its own subscribers.
    /// The child collection becomes effectively read-only and cannot modify the parent.
    /// </summary>
    /// <param name="parent">
    /// The parent <see cref="INexusCollection"/> to connect to. Must be of the same type.
    /// </param>
    /// <param name="token">
    /// A <see cref="CancellationToken"/> that can be used to cancel the connection attempt.
    /// </param>
    /// <returns>
    /// A <see cref="Task{TResult}"/> that completes with <c>true</c> if the connection
    /// was successfully established; otherwise <c>false</c>.
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
    /// Task that is completed when the collection is ready. Once disconnected,
    /// the task is replaced with a new task that will fire when ready again.
    /// </summary>
    public Task ReadyTask { get; }
    
    /// <summary>
    /// Event raised when the collection has been changed by the server.
    /// </summary>
    public ISubscriptionEvent<NexusCollectionChangedEventArgs> Changed { get; }
}
