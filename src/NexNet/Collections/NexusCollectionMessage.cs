using System.Collections.Concurrent;
using System.Threading;
using MemoryPack;
using NexNet.Pipes.Broadcast;

namespace NexNet.Collections;


internal abstract class NexusCollectionMessage<TMessage, TUnion> : INexusCollectionUnion<TUnion>
    where TMessage : NexusCollectionMessage<TMessage, TUnion>, TUnion, new()
    where TUnion : class, INexusCollectionUnion<TUnion>
{
    private static readonly ConcurrentBag<TMessage> _cache = [];
    private int _remaining;

    [MemoryPackOrder(0)] 
    public NexusCollectionMessageFlags Flags { get; set; }

    public static TMessage Rent()
    {
        if (!_cache.TryTake(out var message))
        {
            message = new TMessage();
        }
        else
        {
            // Reset any flags on cached items.
            message.Flags = NexusCollectionMessageFlags.Unset;
        }

        return message;
    }

    public virtual void Return()
    {
        _cache.Add((TMessage)this);
    }

    public void CompleteBroadcast()
    {
        if (Interlocked.Decrement(ref _remaining) == 0)
        {
            Return();
        }
    }

    public abstract TUnion Clone();

    public INexusCollectionBroadcasterMessageWrapper<TUnion> Wrap(INexusBroadcastSession<TUnion>? client = null)
    {
        return NexusCollectionBroadcasterMessageWrapper<TUnion>.Rent((TMessage)this, client);
    }
}
    
