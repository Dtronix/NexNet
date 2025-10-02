using System;
using MemoryPack;

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
internal partial interface INexusCollectionListMessage : INexusCollectionMessage
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

    public override INexusCollectionMessage Clone()    {
        var clone = Rent();
        clone.Flags = Flags;
        clone.Version = Version;
        clone.TotalValues = TotalValues;
        return clone;
        
    }
}

[MemoryPackable(SerializeLayout.Explicit)]
internal partial class NexusCollectionListResetCompleteMessage :
    NexusCollectionMessage<NexusCollectionListResetCompleteMessage>, INexusCollectionListMessage
{
    
    public override INexusCollectionMessage Clone()    {
        var clone = Rent();
        clone.Flags = Flags;
        return clone;
    }
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
    
    
    public override INexusCollectionMessage Clone()    {
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
    
    public override INexusCollectionMessage Clone()    {
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
    
    public override INexusCollectionMessage Clone()    {
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
    
    public override INexusCollectionMessage Clone() 
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
    
    public override INexusCollectionMessage Clone()
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
    
    public override INexusCollectionMessage Clone()  
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
    public override INexusCollectionMessage Clone()  
    {
        var clone = Rent();
        clone.Flags = Flags;
        return clone;
    }
}
