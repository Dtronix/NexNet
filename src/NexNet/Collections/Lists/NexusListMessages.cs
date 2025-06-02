using System;
using System.Runtime.CompilerServices;
using MemoryPack;
using NexNet.Messages;

namespace NexNet.Collections.Lists;

[MemoryPackable]
[MemoryPackUnion(0, typeof(NexusListInsertMessage))]
[MemoryPackUnion(1, typeof(NexusListModifyMessage))]
[MemoryPackUnion(2, typeof(NexusListAddItemMessage))]
[MemoryPackUnion(3, typeof(NexusListMoveMessage))]
[MemoryPackUnion(4, typeof(NexusListRemoveMessage))]
[MemoryPackUnion(5, typeof(NexusListStartResetMessage))]
[MemoryPackUnion(6, typeof(NexusListCompleteResetMessage))]
[MemoryPackUnion(7, typeof(NexusCollectionAckMessage))]
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
internal partial class NexusListStartResetMessage : NexusCollectionMessage<NexusListStartResetMessage>, INexusListMessage
{
    [MemoryPackOrder(1)]
    public int Version { get; set; }
    
    [MemoryPackOrder(2)]
    public int Count { get; set; }
}

[MemoryPackable(SerializeLayout.Explicit)]
internal partial class NexusListCompleteResetMessage :
    NexusCollectionMessage<NexusListCompleteResetMessage>, INexusListMessage
{
    
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

[MemoryPackable(SerializeLayout.Explicit)]
internal partial class NexusCollectionAckMessage :
    NexusCollectionMessage<NexusCollectionAckMessage>, INexusListMessage
{
}

