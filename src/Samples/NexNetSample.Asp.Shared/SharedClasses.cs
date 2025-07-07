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


[GenerateStructureHash(Hash = 12345, Properties = [
    "asfasf",
    "asfasfas"])]
[MemoryPackable(SerializeLayout.Explicit)]
internal partial class ComplicatedMessage {
    [MemoryPackOrder(0)] public Nullable<int>[] Value1 { get; set; }
    [MemoryPackOrder(1)] public int?[] Value2 { get; set; }
    [MemoryPackOrder(2)] public Nullable<int>[]? Value3 { get; set; }
    [MemoryPackOrder(3)] public int?[]? Value4 { get; set; }
    [MemoryPackOrder(4)] public int Value5 { get; set; }
    [MemoryPackOrder(5)] public short? Value6 { get; set; }
    [MemoryPackOrder(6)] public int?[] Value7 { get; set; }
    [MemoryPackOrder(7)] public Nullable<long> Value8 { get; set; }
    [MemoryPackOrder(8)] public int?[]? Value9 { get; set; }
    [MemoryPackOrder(9)] public Nullable<int> Value10;
    [MemoryPackOrder(10)] public int? Value11;
    [MemoryPackOrder(11)] public int Value12 { get; set; }
    [MemoryPackOrder(12)] public ValuesMessage Value13 { get; set; }
    [MemoryPackOrder(13)] public Tuple<ValueTuple<VersionMessage>>?[]? Value14 { get; set; }
    [MemoryPackOrder(14)] public Tuple<ValueTuple<VersionMessage>>[]? Value15 { get; set; }
    [MemoryPackOrder(15)] public Tuple<ValueTuple<Uri>> Value16 { get; set; }
    [MemoryPackOrder(16)] public Tuple<ValueTuple<System.Text.Rune>> Value17 { get; set; }
    [MemoryPackOrder(17)] public ValueTuple<VersionMessage> Value18 { get; set; }
    [MemoryPackOrder(18)] public List<ValueObjects> Value19 { get; set; }
    [MemoryPackOrder(19)] public List<ValueTuple<List<Dictionary<byte, VersionMessage>>, string?, string,int>> Value20 { get; set; }
    [MemoryPackOrder(20)] public int Value21 { get; set; }
}

[MemoryPackable(SerializeLayout.Explicit)]
internal partial class VersionMessage {
    [MemoryPackOrder(0)] public int Value1 { get; set; }
    [MemoryPackOrder(1)] public int Value2 { get; set; }
    [MemoryPackOrder(2)] public ValuesMessage Value3 { get; set; }
    [MemoryPackOrder(3)] public DateTime Value4 { get; set; }
}
[MemoryPackable(SerializeLayout.Explicit)]
internal partial class ValuesMessage {
    [MemoryPackOrder(0)] public byte[] Value1 { get; set; }
    [MemoryPackOrder(1)] public ValueObjects Value2 { get; set; }
    [MemoryPackOrder(2)] public ValuesMessage Value3 { get; set; }
}
[MemoryPackable(SerializeLayout.Explicit)]
internal partial class ValueObjects {
    [MemoryPackOrder(0)] public string[]? Values { get; set; }
}

public class GenerateStructureHashAttribute : Attribute
{
    public int Hash { get; set; }
    public string[] Properties { get; set; }
}
