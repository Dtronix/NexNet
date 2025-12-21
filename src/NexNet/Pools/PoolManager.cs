using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using NexNet.Internals;
using NexNet.Internals.Pipelines.Buffers;
using NexNet.Messages;

namespace NexNet.Pools;

/// <summary>
/// Manages pools for session-related objects.
/// </summary>
internal class PoolManager
{
    /// <summary>
    /// MessagePoolOffsetModifier is used to offset the index of the _messagePools array.
    /// This reduces the starting offset of the array by 100.
    /// </summary>
    private const int MessagePoolOffsetModifier = 100;

    /// <summary>
    /// Pool for registered invocation states.
    /// </summary>
    public readonly ResettablePool<RegisteredInvocationState> RegisteredInvocationStatePool = new(128);

    /// <summary>
    /// Pool for pipe managers.
    /// </summary>
    public readonly PipeManagerPool PipeManagerPool = new();

    /// <summary>
    /// Pool for cancellation token sources.
    /// </summary>
    public readonly CancellationTokenSourcePool CancellationTokenSourcePool = new();

    /// <summary>
    /// Pool for buffer writers.
    /// </summary>
    public readonly ConcurrentBag<BufferWriter<byte>> BufferWriterPool = new();

    private readonly IPooledMessage?[] _messagePools = new IPooledMessage?[50];

    /// <summary>
    /// Initializes the pool manager with message pools.
    /// </summary>
    public PoolManager()
    {
        _messagePools[((int)ClientGreetingMessage.Type - MessagePoolOffsetModifier)] =
            new MessagePool<ClientGreetingMessage>();

        _messagePools[((int)ClientGreetingReconnectionMessage.Type - MessagePoolOffsetModifier)] =
            new MessagePool<ClientGreetingReconnectionMessage>();

        _messagePools[((int)ServerGreetingMessage.Type - MessagePoolOffsetModifier)] =
            new MessagePool<ServerGreetingMessage>();

        _messagePools[((int)InvocationMessage.Type - MessagePoolOffsetModifier)] =
            new MessagePool<InvocationMessage>();

        _messagePools[((int)InvocationCancellationMessage.Type - MessagePoolOffsetModifier)] =
            new MessagePool<InvocationCancellationMessage>();

        _messagePools[((int)InvocationResultMessage.Type - MessagePoolOffsetModifier)] =
            new MessagePool<InvocationResultMessage>();

        _messagePools[((int)DuplexPipeUpdateStateMessage.Type - MessagePoolOffsetModifier)] =
            new MessagePool<DuplexPipeUpdateStateMessage>();
    }

    /// <summary>
    /// Gets the typed message pool for a specific message type.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public MessagePool<T> Pool<T>()
        where T : class, IMessageBase, new()
    {
        return Unsafe.As<MessagePool<T>>(_messagePools[((int)T.Type) - MessagePoolOffsetModifier])!;
    }

    /// <summary>
    /// Gets the message pool for a specific message type.
    /// </summary>
    public IPooledMessage Pool(MessageType type)
    {
        return _messagePools[((int)type) - MessagePoolOffsetModifier]!;
    }

    /// <summary>
    /// Rents a message of the specified type.
    /// </summary>
    public T Rent<T>()
        where T : class, IMessageBase, new()
    {
        return Pool<T>().Rent();
    }

    /// <summary>
    /// Deserializes a message from the buffer.
    /// </summary>
    public IMessageBase Deserialize(MessageType type, ReadOnlySequence<byte> sequence)
    {
        return _messagePools[((int)type) - MessagePoolOffsetModifier]!.DeserializeInterface(sequence);
    }

    /// <summary>
    /// Clears all pools.
    /// </summary>
    public virtual void Clear()
    {
        foreach (var pool in _messagePools)
            pool?.Clear();

        RegisteredInvocationStatePool.Clear();
        CancellationTokenSourcePool.Clear();
        PipeManagerPool.Clear();
    }
}
