using System;
using System.Threading;
using System.Threading.Tasks;
using NexNet.Collections;
using NexNet.Internals.Threading;
using NexNet.Logging;

namespace NexNet.Pipes.Broadcast;

/// <summary>
/// Base class for broadcast client and server implementations providing shared infrastructure
/// for message processing, lifecycle management, and synchronization.
/// </summary>
/// <typeparam name="TUnion">The union type representing all message types that can be broadcast.</typeparam>
internal abstract class NexusBroadcastBase<TUnion>
    where TUnion : class, INexusCollectionUnion<TUnion>
{
    /// <summary>
    /// Processes incoming messages from the broadcast channel.
    /// </summary>
    protected readonly NexusBroadcastMessageProcessor<TUnion> Processor;

    /// <summary>
    /// The unique identifier for this broadcast channel.
    /// </summary>
    protected readonly ushort Id;

    /// <summary>
    /// The collection mode (unidirectional or bidirectional).
    /// </summary>
    protected readonly NexusCollectionMode Mode;

    /// <summary>
    /// Logger for this broadcast instance.
    /// </summary>
    protected readonly INexusLogger? Logger;

    /// <summary>
    /// Event raised when the collection changes.
    /// </summary>
    protected readonly SubscriptionEvent<NexusCollectionChangedEventArgs> CoreChangedEvent;

    /// <summary>
    /// Cancellation token source for stopping background operations.
    /// </summary>
    protected CancellationTokenSource? StopCts;

    /// <summary>
    /// Semaphore for serializing operations on the broadcast session.
    /// </summary>
    protected SemaphoreSlim? OperationSemaphore;

    /// <summary>
    /// Initializes a new instance of the broadcast base class.
    /// </summary>
    /// <param name="id">The unique identifier for this broadcast channel.</param>
    /// <param name="mode">The collection mode (unidirectional or bidirectional).</param>
    /// <param name="logger">Optional logger instance.</param>
    /// <param name="loggerPrefix">Prefix to prepend to the logger name.</param>
    protected NexusBroadcastBase(ushort id, NexusCollectionMode mode, INexusLogger? logger, string loggerPrefix)
    {
        Id = id;
        Mode = mode;
        Logger = logger?.CreateLogger($"{loggerPrefix}{id}");
        CoreChangedEvent = new SubscriptionEvent<NexusCollectionChangedEventArgs>();
        Processor = new NexusBroadcastMessageProcessor<TUnion>(Logger, OnProcessCore);
    }

    /// <summary>
    /// Acquires an exclusive lock for operations on the broadcast session.
    /// </summary>
    /// <returns>A disposable that releases the lock when disposed.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the broadcast session is not active.</exception>
    protected async ValueTask<IDisposable> OperationLock()
    {
        if (OperationSemaphore == null)
            throw new InvalidOperationException("Broadcast session is not active.");

        await OperationSemaphore.WaitAsync().ConfigureAwait(false);
        return new SemaphoreSlimDisposable(OperationSemaphore);
    }

    /// <summary>
    /// Processes a message received from the broadcast channel.
    /// </summary>
    /// <param name="message">The message to process.</param>
    /// <param name="sourceClient">The client that sent the message, or null for server-originated messages.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The result of processing, including whether to acknowledge and disconnect.</returns>
    protected abstract BroadcastMessageProcessResult OnProcessCore(
        TUnion message,
        INexusBroadcastSession<TUnion>? sourceClient,
        CancellationToken ct);
}
