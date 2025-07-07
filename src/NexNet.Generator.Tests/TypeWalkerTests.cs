using Microsoft.CodeAnalysis;
using NUnit.Framework;

namespace NexNet.Generator.Tests;

public class TypeWalkerTests
{
    [Test]
    public void TypeWalker()
    {
        var diagnostic = CSharpGeneratorRunner.RunTypeWalkerGenerator("""
using NexNet;
using MemoryPack;
using System;
namespace NexNetDemo;
[GenerateStructureHash]
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
    [MemoryPackOrder(16)] public Tuple<ValueTuple<Rune>> Value17 { get; set; }
    [MemoryPackOrder(17)] public ValueTuple<VersionMessage> Value18 { get; set; }
    [MemoryPackOrder(18)] public List<ValueObjects> Value19 { get; set; }
    [MemoryPackOrder(19)] public List<ValueTuple<List<Dictionary<byte, VersionMessage>, string?, string>,int>> Value20 { get; set; }
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
""", minDiagnostic:DiagnosticSeverity.Warning);
        Assert.That(diagnostic.Any(d => d.Id == DiagnosticDescriptors.CancellationTokenOnVoid.Id), Is.True);
    }

    [Test]
    public void TypeWalkerSimple()
    {
        var diagnostic = CSharpGeneratorRunner.RunTypeWalkerGenerator("""
using NexNet;
using MemoryPack;
using System;
namespace NexNetDemo;
[GenerateStructureHash(Hash = 12345, Properties = [
"asfasf",
"asfasfas"])]
internal partial class SimpleMessage {
    [MemoryPackOrder(0)] public Nullable<int>[] Value1 { get; set; }
    [MemoryPackOrder(1)] public int?[] Value2 { get; set; }
    [MemoryPackOrder(2)] public Nullable<int>[]? Value3 { get; set; }
    [MemoryPackOrder(3)] public int?[]? Value4 { get; set; }
    [MemoryPackOrder(4)] public Nullable<int> Value5 { get; set; }
    [MemoryPackOrder(5)] public int? Value6 { get; set; }
}
public class GenerateStructureHashAttribute : Attribute
{
    public int Hash { get; set; }
    public string[] Properties { get; set; }
}
""", minDiagnostic:DiagnosticSeverity.Warning);
        Diagnostic.SimpleDiagnostic
        var first = diagnostic.FirstOrDefault(d => d.Id == "CS8785");
        Assert.That(first, Is.Null, first.ToString());
    }
}
