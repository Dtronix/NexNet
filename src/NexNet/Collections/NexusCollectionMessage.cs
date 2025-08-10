using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading;
using MemoryPack;

namespace NexNet.Collections;

internal abstract class NexusCollectionMessage<T> : INexusCollectionMessage
    where T : NexusCollectionMessage<T>, new()
{
    public static readonly ConcurrentBag<NexusCollectionMessage<T>> Cache = new();
    private int _remaining;
    
    [MemoryPackOrder(0)]
    public NexusCollectionMessageFlags Flags { get; set; }

    public static T Rent()
    {
        if(!Cache.TryTake(out var operation))
            operation = new T();

        return Unsafe.As<T>(operation);
    }

    public virtual void ReturnToCache()
    {
        Cache.Add(this);
    }

    public void CompleteBroadcast()
    {
        if (Interlocked.Decrement(ref _remaining) == 0)
        {
            ReturnToCache();
        }
    }

    [MemoryPackIgnore]
    public int Remaining
    {
        get => _remaining;
        set => _remaining = value;
    }

    public abstract INexusCollectionMessage Clone();
}
