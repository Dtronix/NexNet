using System.Collections.Concurrent;
using System.Threading;
using NexNet.Pipes.Broadcast;
using NexNet.Serialization;

namespace NexNet.Collections;


internal abstract class NexusCollectionMessage<TMessage, TUnion> : INexusCollectionUnion<TUnion>
    where TMessage : NexusCollectionMessage<TMessage, TUnion>, TUnion, new()
    where TUnion : class, INexusCollectionUnion<TUnion>
{
    private static readonly ConcurrentBag<TMessage> _cache = [];
    private int _remaining;

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

    /// <summary>
    /// Writes the message body as a fixed-length MessagePack array whose first element is <see cref="Flags"/>.
    /// </summary>
    public abstract void SerializeBody(ref MsgPackWriter writer);

    /// <summary>
    /// Populates this message from a body written by <see cref="SerializeBody"/>.
    /// </summary>
    public abstract void DeserializeBody(ref MsgPackReader reader);

    public INexusCollectionBroadcasterMessageWrapper<TUnion> Wrap(INexusBroadcastSession<TUnion>? client = null)
    {
        return NexusCollectionBroadcasterMessageWrapper<TUnion>.Rent((TMessage)this, client);
    }
}

