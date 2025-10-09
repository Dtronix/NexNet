using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading;
using MemoryPack;
using NexNet.Pipes.Broadcast;

namespace NexNet.Collections.Lists;


[MemoryPackable]
[MemoryPackUnion(0, typeof(NexusCollectionListResetStartMessage))]        
[MemoryPackUnion(1, typeof(NexusCollectionListResetValuesMessage))]      
[MemoryPackUnion(2, typeof(NexusCollectionListResetCompleteMessage))]                
[MemoryPackUnion(3, typeof(NexusCollectionListClearMessage))]                
[MemoryPackUnion(4, typeof(NexusCollectionListInsertMessage))]
[MemoryPackUnion(5, typeof(NexusCollectionListReplaceMessage))]
[MemoryPackUnion(6, typeof(NexusCollectionListMoveMessage))]
[MemoryPackUnion(7, typeof(NexusCollectionListRemoveMessage))]
[MemoryPackUnion(8, typeof(NexusCollectionListNoopMessage))]
internal partial interface INexusCollectionListMessage : INexusCollectionUnion<INexusCollectionListMessage>
{
    
}


[MemoryPackable(SerializeLayout.Explicit)]
internal partial class NexusCollectionListResetStartMessage 
    : NexusCollectionMessage<NexusCollectionListResetStartMessage>, INexusCollectionListMessage
{
    [MemoryPackOrder(1)]
    public int Version { get; set; }
    
    [MemoryPackOrder(2)]
    public int TotalValues { get; set; }

    public override NexusCollectionListResetStartMessage Clone()
    {
        throw new NotImplementedException();
    }

    public INexusCollectionBroadcasterMessageWrapper<INexusCollectionListMessage> Wrap(INexusBroadcastSession<INexusCollectionListMessage>? client = null)
    {
        throw new NotImplementedException();
    }

    INexusCollectionListMessage INexusCollectionUnion<INexusCollectionListMessage>.Clone()
    {
        return Clone();
    }
}

[MemoryPackable(SerializeLayout.Explicit)]
internal partial class NexusCollectionListResetCompleteMessage :
    NexusCollectionMessage<NexusCollectionListResetCompleteMessage>, INexusCollectionListMessage
{


}

[MemoryPackable(SerializeLayout.Explicit)]
internal partial class NexusCollectionListResetValuesMessage
    : NexusCollectionValueMessage<NexusCollectionListResetValuesMessage>, INexusCollectionListMessage
{
    [MemoryPackOrder(1)]
    [MemoryPoolFormatter<byte>]
    public Memory<byte> Values
    {
        get => base.ValueCore;
        set => base.ValueCore = value;
    }
    
    
    public override INexusCollectionUnion<> Clone()   
    {
        var clone = Rent();
        clone.Flags = Flags;
        
        // Reference the values only as we don't need a deep copy of the values.
        clone.Values = Values;
        return clone;
        
    }

    [MemoryPackOnDeserialized]
    private void OnDeserialized() => base.OnDeserializedCore();
}



/// <summary>
/// Contains an invocation request message data.
/// </summary>
[MemoryPackable(SerializeLayout.Explicit)]
internal partial class NexusCollectionListInsertMessage
    : NexusCollectionValueMessage<NexusCollectionListInsertMessage>, INexusCollectionListMessage
{
    
    [MemoryPackOrder(1)]
    public int Version { get; set; }

    [MemoryPackOrder(2)]
    public int Index { get; set; }
    
    [MemoryPackOrder(3)]
    [MemoryPoolFormatter<byte>]
    public Memory<byte> Value
    {
        get => base.ValueCore;
        set => base.ValueCore = value;
    }
    
    public override INexusCollectionUnion<> Clone()    {
        var clone = Rent();
        clone.Flags = Flags;
        clone.Version = Version;
        clone.Index = Index;
        clone.Value = Value;
        return clone;
    }

    [MemoryPackOnDeserialized]
    private void OnDeserialized() => base.OnDeserializedCore();
}

[MemoryPackable(SerializeLayout.Explicit)]
internal partial class NexusCollectionListReplaceMessage
    : NexusCollectionValueMessage<NexusCollectionListReplaceMessage>, INexusCollectionListMessage
{
    [MemoryPackOrder(1)]
    public int Version { get; set; }

    [MemoryPackOrder(2)]
    public int Index { get; set; }
    
    [MemoryPackOrder(3)]
    [MemoryPoolFormatter<byte>]
    public Memory<byte> Value
    {
        get => base.ValueCore;
        set => base.ValueCore = value;
    }
    
    public override INexusCollectionUnion<> Clone()    {
        var clone = Rent();
        clone.Flags = Flags;
        clone.Version = Version;
        clone.Index = Index;
        clone.Value = Value;
        return clone;
    }

    [MemoryPackOnDeserialized]
    private void OnDeserialized() => base.OnDeserializedCore();
}

[MemoryPackable(SerializeLayout.Explicit)]
internal partial class NexusCollectionListMoveMessage 
    : NexusCollectionMessage<NexusCollectionListMoveMessage>, INexusCollectionListMessage
{
    [MemoryPackOrder(1)]
    public int Version { get; set; }
    
    [MemoryPackOrder(2)]
    public int FromIndex { get; set; }
    
    [MemoryPackOrder(3)]
    public int ToIndex { get; set; }
    
    public override INexusCollectionUnion<> Clone() 
    {
        var clone = Rent();
        clone.Flags = Flags;
        clone.Version = Version;
        clone.FromIndex = FromIndex;
        clone.ToIndex = ToIndex;
        return clone;
    }
}


[MemoryPackable(SerializeLayout.Explicit)]
internal partial class NexusCollectionListClearMessage :
    NexusCollectionMessage<NexusCollectionListClearMessage>, INexusCollectionListMessage
{
    [MemoryPackOrder(1)]
    public int Version { get; set; }
    
    public override INexusCollectionUnion<> Clone()
    {
        var clone = Rent();
        clone.Flags = Flags;
        clone.Version = Version;
        return clone;
    }
}



[MemoryPackable(SerializeLayout.Explicit)]
internal partial class NexusCollectionListRemoveMessage :
    NexusCollectionMessage<NexusCollectionListRemoveMessage>, INexusCollectionListMessage
{
    [MemoryPackOrder(1)]
    public int Version { get; set; }
    
    [MemoryPackOrder(2)]
    public int Index { get; set; }
    
    public override INexusCollectionUnion<> Clone()  
    {
        var clone = Rent();
        clone.Flags = Flags;
        clone.Version = Version;
        clone.Index = Index;
        return clone;
    }
}

[MemoryPackable(SerializeLayout.Explicit)]
internal partial class NexusCollectionListNoopMessage :
    NexusCollectionMessage<NexusCollectionListNoopMessage>, INexusCollectionListMessage
{
    public override INexusCollectionUnion<> Clone()  
    {
        var clone = Rent();
        clone.Flags = Flags;
        return clone;
    }
}


internal abstract class NexusCollectionMessage<TMessage>: INexusCollectionUnion<TMessage>
    where TMessage : NexusCollectionMessage<TMessage>, INexusCollectionUnion<TMessage>, new() 
{
    public static readonly ConcurrentBag<NexusCollectionMessage<TMessage>> Cache = new();
    private int _remaining;
    
    [MemoryPackOrder(0)]
    public NexusCollectionMessageFlags Flags { get; set; }

    public static TMessage Rent()
    {
        if(!Cache.TryTake(out var message))
            message = new TMessage();

        message.Flags = NexusCollectionMessageFlags.Ack;
        return Unsafe.As<TMessage>(message);
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

    public abstract TMessage Clone();

    public INexusCollectionBroadcasterMessageWrapper<TMessage> Wrap(INexusBroadcastSession<TMessage>? client = null) =>
        NexusCollectionBroadcasterMessageWrapper<TMessage>.Rent((TMessage)this, client);
}
