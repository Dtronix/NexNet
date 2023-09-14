using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using NexNet.Internals;
using NexNet.Messages;
using Pipelines.Sockets.Unofficial.Buffers;

namespace NexNet.Cache;

internal class CacheManager
{

    /// <summary>
    /// MessageCacheOffsetModifier is a constant used in the CacheManager class to offset the index of the _messageCaches array.
    /// This offset is applied when accessing the cache for a specific message type. The offset reduces the staring offset of the array by 100.
    /// </summary>
    private const int MessageCacheOffsetModifier = 100;

    public readonly CachedResettableItem<RegisteredInvocationState> RegisteredInvocationStateCache = new();
    public readonly CachedPipeManager PipeManagerCache = new();
    public readonly CachedCts CancellationTokenSourceCache = new();
    //public readonly CachedDuplexPipe NexusDuplexPipeCache = new();
    //public readonly CachedRentedDuplexPipe NexusRentedDuplexPipeCache = new();
    public readonly ConcurrentBag<BufferWriter<byte>> BufferWriterCache = new();

    private readonly ICachedMessage?[] _messageCaches = new ICachedMessage?[50];

    public CacheManager()
    {
        // 
        _messageCaches[((int)ClientGreetingMessage.Type - MessageCacheOffsetModifier)] =
            new CachedCachedMessage<ClientGreetingMessage>();

        _messageCaches[((int)ServerGreetingMessage.Type - MessageCacheOffsetModifier)] =
            new CachedCachedMessage<ServerGreetingMessage>();

        _messageCaches[((int)InvocationMessage.Type - MessageCacheOffsetModifier)] =
            new CachedCachedMessage<InvocationMessage>();

        _messageCaches[((int)InvocationCancellationMessage.Type - MessageCacheOffsetModifier)] =
            new CachedCachedMessage<InvocationCancellationMessage>();

        _messageCaches[((int)InvocationResultMessage.Type - MessageCacheOffsetModifier)] =
            new CachedCachedMessage<InvocationResultMessage>();

        _messageCaches[((int)DuplexPipeUpdateStateMessage.Type - MessageCacheOffsetModifier)] =
            new CachedCachedMessage<DuplexPipeUpdateStateMessage>();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public CachedCachedMessage<T> Cache<T>()
        where T : class, IMessageBase, new()
    {
        // Offset the messages -100 for a smaller array.
        return Unsafe.As<CachedCachedMessage<T>>(_messageCaches[((int)T.Type) - MessageCacheOffsetModifier])!;
    }

    public ICachedMessage Cache(MessageType type)
    {
        return _messageCaches[((int)type) - MessageCacheOffsetModifier]!;
    }

    public T Rent<T>()
        where T : class, IMessageBase, new()
    {
        return Cache<T>().Rent();
    }

    public IMessageBase Deserialize(MessageType type, ReadOnlySequence<byte> sequence)
    {
        return _messageCaches[((int)type) - MessageCacheOffsetModifier]!.DeserializeInterface(sequence);
    }

    public virtual void Clear()
    {
        foreach (var t in _messageCaches)
            t?.Clear();

        RegisteredInvocationStateCache.Clear();
        CancellationTokenSourceCache.Clear();
        PipeManagerCache.Clear();
    }
}
