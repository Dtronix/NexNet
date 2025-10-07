using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading;
using MemoryPack;
using NexNet.Collections.Lists;
using NexNet.Pipes.Broadcast;

namespace NexNet.Collections;

internal abstract class NexusCollectionMessage<T>: INexusCollectionMessage
    where T : NexusCollectionMessage<T>, new() 
{
    public static readonly ConcurrentBag<NexusCollectionMessage<T>> Cache = new();
    private int _remaining;
    
    [MemoryPackOrder(0)]
    public NexusCollectionMessageFlags Flags { get; set; }

    public static T Rent()
    {
        if(!Cache.TryTake(out var message))
            message = new T();

        message.Flags = NexusCollectionMessageFlags.Ack;
        return Unsafe.As<T>(message);
    }

    public virtual void Return()
    {
        Cache.Add(this);
    }

    public void CompleteBroadcast()
    {
        if (Interlocked.Decrement(ref _remaining) == 0)
        {
            Return();
        }
    }
    
    [MemoryPackIgnore]
    public int Remaining
    {
        get => _remaining;
        set => _remaining = value;
    }

    public abstract INexusCollectionMessage Clone();
    
    public INexusCollectionBroadcasterMessageWrapper Wrap(INexusBroadcastSession? client = null) 
        => NexusCollectionBroadcasterMessageWrapper.Rent((T)this, client);
}
