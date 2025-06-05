using System;
using System.Runtime.CompilerServices;
using MemoryPack;
using NexNet.Messages;

namespace NexNet.Collections.Lists;

[MemoryPackable]
[MemoryPackUnion(0, typeof(NexusCollectionAckMessage))]         
[MemoryPackUnion(1, typeof(NexusCollectionResetStartMessage))]        
[MemoryPackUnion(2, typeof(NexusCollectionResetValuesMessage))]      
[MemoryPackUnion(3, typeof(NexusCollectionResetCompleteMessage))]                
internal partial interface INexusListMessage2 : INexusCollectionMessage
{

}


[MemoryPackable]
[MemoryPackUnion(0, typeof(NexusCollectionAckMessage))]         
[MemoryPackUnion(1, typeof(NexusCollectionResetStartMessage))]        
[MemoryPackUnion(2, typeof(NexusCollectionResetValuesMessage))]      
[MemoryPackUnion(3, typeof(NexusCollectionResetCompleteMessage))]                
[MemoryPackUnion(4, typeof(NexusListInsertMessage))]
[MemoryPackUnion(5, typeof(NexusListModifyMessage))]
[MemoryPackUnion(6, typeof(NexusListAddItemMessage))]
[MemoryPackUnion(7, typeof(NexusListMoveMessage))]
[MemoryPackUnion(8, typeof(NexusListRemoveMessage))]
internal partial interface INexusListMessage : INexusCollectionMessage
{

}

/// <summary>
/// Contains an invocation request message data.
/// </summary>
[MemoryPackable(SerializeLayout.Explicit)]
internal partial class NexusListInsertMessage : NexusCollectionValueMessage<NexusListInsertMessage>, INexusListMessage
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

    [MemoryPackOnDeserialized]
    private void OnDeserialized() => base.OnDeserializedCore();


}

[MemoryPackable(SerializeLayout.Explicit)]
internal partial class NexusListModifyMessage : NexusCollectionValueMessage<NexusListModifyMessage>, INexusListMessage
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

    [MemoryPackOnDeserialized]
    private void OnDeserialized() => base.OnDeserializedCore();
}

[MemoryPackable(SerializeLayout.Explicit)]
internal partial class NexusListAddItemMessage : NexusCollectionValueMessage<NexusListAddItemMessage>, INexusListMessage
{
    [MemoryPackOrder(1)]
    [MemoryPoolFormatter<byte>]
    public Memory<byte> Value
    {
        get => base.ValueCore;
        set => base.ValueCore = value;
    }

    [MemoryPackOnDeserialized]
    private void OnDeserialized() => base.OnDeserializedCore();
}

[MemoryPackable(SerializeLayout.Explicit)]
internal partial class NexusListMoveMessage : NexusCollectionMessage<NexusListMoveMessage>, INexusListMessage
{
    [MemoryPackOrder(1)]
    public int Version { get; set; }
    
    [MemoryPackOrder(2)]
    public int FromIndex { get; set; }
    
    [MemoryPackOrder(3)]
    public int ToIndex { get; set; }
}


[MemoryPackable(SerializeLayout.Explicit)]
internal partial class NexusListClearMessage :
    NexusCollectionMessage<NexusListClearMessage>, INexusListMessage
{
    [MemoryPackOrder(1)]
    public int Version { get; set; }
}



[MemoryPackable(SerializeLayout.Explicit)]
internal partial class NexusListRemoveMessage :
    NexusCollectionMessage<NexusListRemoveMessage>, INexusListMessage
{
    [MemoryPackOrder(1)]
    public int Version { get; set; }
    
    [MemoryPackOrder(2)]
    public int Index { get; set; }
}

