using System;
using MemoryPack;
using NexNet.Collections.Lists;

namespace NexNet.Collections;

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
internal partial interface INexusCollectionMessage
{
    int Id { get; set; }
    void ReturnToCache();

    void CompleteBroadcast();

    [MemoryPackIgnore]
    int Remaining { get; set; }
}

[MemoryPackable(SerializeLayout.Explicit)]
internal partial class NexusCollectionAckMessage :
    NexusCollectionMessage<NexusCollectionAckMessage> 
{
}



[MemoryPackable(SerializeLayout.Explicit)]
internal partial class NexusCollectionResetStartMessage 
    : NexusCollectionMessage<NexusCollectionResetStartMessage>
{

}

[MemoryPackable(SerializeLayout.Explicit)]
internal partial class NexusCollectionResetCompleteMessage :
    NexusCollectionMessage<NexusCollectionResetCompleteMessage>
{
    
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

    [MemoryPackOnDeserialized]
    private void OnDeserialized() => base.OnDeserializedCore();


}

[MemoryPackable(SerializeLayout.Explicit)]
internal partial class NexusListModifyMessage : NexusCollectionValueMessage<NexusListModifyMessage>
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
internal partial class NexusListAddItemMessage : NexusCollectionValueMessage<NexusListAddItemMessage>
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
internal partial class NexusListMoveMessage : NexusCollectionMessage<NexusListMoveMessage>
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
    NexusCollectionMessage<NexusListClearMessage>
{
    [MemoryPackOrder(1)]
    public int Version { get; set; }
}



[MemoryPackable(SerializeLayout.Explicit)]
internal partial class NexusListRemoveMessage :
    NexusCollectionMessage<NexusListRemoveMessage>
{
    [MemoryPackOrder(1)]
    public int Version { get; set; }
    
    [MemoryPackOrder(2)]
    public int Index { get; set; }
}

