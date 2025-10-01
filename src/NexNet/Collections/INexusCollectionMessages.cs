using System;
using MemoryPack;
using NexNet.Collections.Lists;

namespace NexNet.Collections;

/// <summary>
/// Flags that control behavior of nexus collection messages.
/// </summary>
[Flags]
public enum NexusCollectionMessageFlags : byte
{
    /// <summary>
    /// No flags set.
    /// </summary>
    Unset = 0,
    
    /// <summary>
    /// Indicates that the message contains acknowledgment from the server that the operation has been completed.
    /// </summary>
    Ack = 1 << 0
} 

[MemoryPackable]
[MemoryPackUnion(0, typeof(NexusCollectionResetStartMessage))]        
[MemoryPackUnion(1, typeof(NexusCollectionResetValuesMessage))]      
[MemoryPackUnion(2, typeof(NexusCollectionResetCompleteMessage))]                
[MemoryPackUnion(3, typeof(NexusListClearMessage))]                
[MemoryPackUnion(4, typeof(NexusListInsertMessage))]
[MemoryPackUnion(5, typeof(NexusListReplaceMessage))]
[MemoryPackUnion(6, typeof(NexusListMoveMessage))]
[MemoryPackUnion(7, typeof(NexusListRemoveMessage))]
[MemoryPackUnion(8, typeof(NexusListNoopMessage))]
internal partial interface INexusCollectionMessage
{
    public NexusCollectionMessageFlags Flags { get; set; }
    
    [MemoryPackIgnore]
    int Remaining { get; set; }
    
    void ReturnToCache();

    void CompleteBroadcast();

    public INexusCollectionMessage Clone();
}


[MemoryPackable(SerializeLayout.Explicit)]
internal partial class NexusCollectionResetStartMessage 
    : NexusCollectionMessage<NexusCollectionResetStartMessage>
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
internal partial class NexusCollectionResetCompleteMessage :
    NexusCollectionMessage<NexusCollectionResetCompleteMessage>
{
    
    public override INexusCollectionMessage Clone()    {
        var clone = Rent();
        clone.Flags = Flags;
        return clone;
    }
}

[MemoryPackable(SerializeLayout.Explicit)]
internal partial class NexusCollectionResetValuesMessage
    : NexusCollectionValueMessage<NexusCollectionResetValuesMessage>
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
internal partial class NexusListInsertMessage : NexusCollectionValueMessage<NexusListInsertMessage>
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
internal partial class NexusListReplaceMessage : NexusCollectionValueMessage<NexusListReplaceMessage>
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
internal partial class NexusListMoveMessage : NexusCollectionMessage<NexusListMoveMessage>
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
internal partial class NexusListClearMessage :
    NexusCollectionMessage<NexusListClearMessage>
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
internal partial class NexusListRemoveMessage :
    NexusCollectionMessage<NexusListRemoveMessage>
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
internal partial class NexusListNoopMessage :
    NexusCollectionMessage<NexusListNoopMessage>
{
    public override INexusCollectionMessage Clone()  
    {
        var clone = Rent();
        clone.Flags = Flags;
        return clone;
    }
}
