using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using MemoryPack;
using NexNet.Collections.Lists;

namespace NexNet.Collections;

internal abstract class NexusCollectionMessage<T> : INexusCollectionMessage
    where T : NexusCollectionMessage<T>, new()
{
    public static readonly ConcurrentBag<NexusCollectionMessage<T>> Cache = new();
    private int _remaining;
        
    [MemoryPackOrder(0)]
    public int Id { get; set; }

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
}


internal abstract class NexusCollectionValueMessage<T> : NexusCollectionMessage<T>, INexusCollectionValueMessage
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
        ReturnValueToPool();
        base.ReturnToCache();
    }

    public void ReturnValueToPool()
    {
        if (_isArgumentPoolArray)
        {
            // Reset the pool flag.
            _isArgumentPoolArray = false;
            if (MemoryMarshal.TryGetArray<byte>(ValueCore, out var segment) && segment.Array is { Length: > 0 })
                ArrayPool<byte>.Shared.Return(segment.Array, false);

            ValueCore = default;
        }
    }
}

internal interface INexusCollectionValueMessage
{
    void ReturnValueToPool();
}



[MemoryPackable(SerializeLayout.Explicit)]
internal partial class NexusCollectionAckMessage :
    NexusCollectionMessage<NexusCollectionAckMessage>, INexusListMessage 
{
}



[MemoryPackable(SerializeLayout.Explicit)]
internal partial class NexusCollectionResetStartMessage 
    : NexusCollectionMessage<NexusCollectionResetStartMessage>, INexusListMessage
{
    [MemoryPackOrder(1)]
    public int Version { get; set; }
    
    [MemoryPackOrder(2)]
    public int Count { get; set; }
}

[MemoryPackable(SerializeLayout.Explicit)]
internal partial class NexusCollectionResetCompleteMessage :
    NexusCollectionMessage<NexusCollectionResetCompleteMessage>, INexusListMessage
{
    
}

[MemoryPackable(SerializeLayout.Explicit)]
internal partial class NexusCollectionResetValuesMessage 
    : NexusCollectionValueMessage<NexusCollectionResetValuesMessage>, INexusListMessage
{
    [MemoryPackOrder(1)]
    [MemoryPoolFormatter<byte>]
    public Memory<byte> Values
    {
        get => base.ValueCore;
        set => base.ValueCore = value;
    }

    [MemoryPackOnDeserialized]
    private void OnDeserialized() => base.OnDeserializedCore();
}
