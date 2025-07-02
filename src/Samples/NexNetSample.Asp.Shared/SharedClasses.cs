using MemoryPack;
using NexNet;
using NexNet.Collections;
using NexNet.Collections.Lists;
using NexNet.Pipes;

namespace NexNetSample.Asp.Shared;

[NexusVersion(Version = "v3")]
public partial interface IServerNexusV3 : IServerNexusV2
{

}

public partial interface IClientNexus
{
    void ClientVoid();
    void ClientVoidWithParam(int id);
    ValueTask ClientTask();
    ValueTask ClientTaskWithParam(int data);
    ValueTask<int> ClientTaskValue();
    ValueTask<int> ClientTaskValueWithParam(int data);
    ValueTask ClientTaskWithCancellation(CancellationToken cancellationToken);
    ValueTask ClientTaskWithValueAndCancellation(int value, CancellationToken cancellationToken);
    ValueTask<int> ClientTaskValueWithCancellation(CancellationToken cancellationToken);
    ValueTask<int> ClientTaskValueWithValueAndCancellation(int value, CancellationToken cancellationToken);
    ValueTask ClientTaskValueWithDuplexPipe(INexusDuplexPipe pipe);
}

[NexusVersion(Version = "v2", HashLock = -1549245336)]
public partial interface IServerNexusV2 : IServerNexus
{
    [NexusCollection(NexusCollectionMode.BiDrirectional)]
    INexusList<int> IntegerList { get; }
}

[NexusVersion(Version = "v1", HashLock = 10059206)]
public partial interface IServerNexus
{
    void ServerVoid();
    void ServerVoidWithParam(int id);
    ValueTask ServerTask();
    ValueTask ServerTaskWithParam(int data);
    ValueTask<int> ServerTaskValue();
    ValueTask<int> ServerTaskValueWithParam(int data);
    ValueTask ServerTaskWithCancellation(CancellationToken cancellationToken);
    ValueTask ServerTaskWithValueAndCancellation(int value, CancellationToken cancellationToken);
    ValueTask<int> ServerTaskValueWithCancellation(CancellationToken cancellationToken);
    ValueTask<int> ServerTaskValueWithValueAndCancellation(int value, CancellationToken cancellationToken);
    ValueTask ServerTaskValueWithDuplexPipe(INexusDuplexPipe pipe);
    ValueTask ServerData(byte[] data);
}


public class GenerateStructureHashAttribute : Attribute
{

}


[GenerateStructureHash]
internal partial class Message {
    [MemoryPackOrder(0)] public VersionMessage[] Messages { get; set; }
    
    [MemoryPackOrder(1)] public Nullable<int> Messages224 { get; set; }
    [MemoryPackOrder(3)] public Nullable<int>[] Messages224w { get; set; }
    [MemoryPackOrder(4)] public int[] Messages2256 { get; set; }
}

[MemoryPackable(SerializeLayout.Explicit)]
internal partial class VersionMessage {
    [MemoryPackOrder(0)] public int Version { get; set; }
    [MemoryPackOrder(1)] public int TotalValues { get; set; }
    [MemoryPackOrder(2)] public ValuesMessage Values { get; set; }
}
[MemoryPackable(SerializeLayout.Explicit)]
internal partial class ValuesMessage {
    [MemoryPackOrder(0)] public byte[] Values { get; set; }
    [MemoryPackOrder(1)] public ValueObjects ValueObjects { get; set; }
}

[MemoryPackable(SerializeLayout.Explicit)]
internal partial class ValueObjects {
    [MemoryPackOrder(0)] public string[] Values { get; set; }
}


