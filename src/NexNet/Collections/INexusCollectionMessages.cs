﻿using System;
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
[MemoryPackUnion(3, typeof(NexusCollectionClearMessage))]                
[MemoryPackUnion(4, typeof(NexusListInsertMessage))]
[MemoryPackUnion(5, typeof(NexusListReplaceMessage))]
[MemoryPackUnion(6, typeof(NexusListMoveMessage))]
[MemoryPackUnion(7, typeof(NexusListRemoveMessage))]
internal partial interface INexusCollectionMessage
{
    public NexusCollectionMessageFlags Flags { get; set; }
    
    void ReturnToCache();

    void CompleteBroadcast();

    [MemoryPackIgnore]
    int Remaining { get; set; }

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

    public override INexusCollectionMessage Clone() => 
        new NexusCollectionResetStartMessage { Flags = Flags, Version = Version, TotalValues = TotalValues };
}

[MemoryPackable(SerializeLayout.Explicit)]
internal partial class NexusCollectionResetCompleteMessage :
    NexusCollectionMessage<NexusCollectionResetCompleteMessage>
{
    public override INexusCollectionMessage Clone() => new NexusCollectionResetCompleteMessage { Flags = Flags };
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
    
    public override INexusCollectionMessage Clone() => 
        new NexusCollectionResetValuesMessage { Flags = Flags, Values = Values };

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
    
    public override INexusCollectionMessage Clone() => 
        new NexusListInsertMessage { Flags = Flags, Version = Version, Index = Index, Value = Value };

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
    
    public override INexusCollectionMessage Clone() => 
        new NexusListReplaceMessage { Flags = Flags, Version = Version, Index = Index, Value = Value };

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
    
    public override INexusCollectionMessage Clone() => 
        new NexusListMoveMessage { Flags = Flags, Version = Version, FromIndex = FromIndex, ToIndex = ToIndex };
}


[MemoryPackable(SerializeLayout.Explicit)]
internal partial class NexusCollectionClearMessage :
    NexusCollectionMessage<NexusCollectionClearMessage>
{
    [MemoryPackOrder(1)]
    public int Version { get; set; }
    
    public override INexusCollectionMessage Clone() => 
        new NexusCollectionClearMessage { Flags = Flags, Version = Version };
}



[MemoryPackable(SerializeLayout.Explicit)]
internal partial class NexusListRemoveMessage :
    NexusCollectionMessage<NexusListRemoveMessage>
{
    [MemoryPackOrder(1)]
    public int Version { get; set; }
    
    [MemoryPackOrder(2)]
    public int Index { get; set; }
    
    public override INexusCollectionMessage Clone() => 
        new NexusListRemoveMessage { Flags = Flags, Version = Version, Index = Index };
}

