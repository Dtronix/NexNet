using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using MemoryPack;

namespace NexNet.Collections;

internal abstract class NexusCollectionMessage<T> : INexusCollectionMessage
    where T : NexusCollectionMessage<T>, new()
{
    public static readonly ConcurrentBag<NexusCollectionMessage<T>> Cache = new();
    
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
}


internal abstract class NexusCollectionValueMessage<T> : NexusCollectionMessage<T>
    where T : NexusCollectionMessage<T>, new()
{
    private bool _isArgumentPoolArray;

    protected Memory<byte> ValueCore;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TValue? DeserializeValue<TValue>()
    {
        return MemoryPackSerializer.Deserialize<TValue>(ValueCore.Span);
    }
    
    protected void OnDeserializedCore()
    {
        _isArgumentPoolArray = true;
    }

    public override void ReturnToCache()
    {
        if (_isArgumentPoolArray)
        {
            // Reset the pool flag.
            _isArgumentPoolArray = false;
            if (MemoryMarshal.TryGetArray<byte>(ValueCore, out var segment) && segment.Array is { Length: > 0 })
                ArrayPool<byte>.Shared.Return(segment.Array, false);

            ValueCore = default;
        }

        base.ReturnToCache();
    }
}
