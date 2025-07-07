using Microsoft.CodeAnalysis;
using NUnit.Framework;

namespace NexNet.Generator.Tests;

public class TypeWalkerTests
{
    [Test]
    public void TypeWalker()
    {
        Run("""
            using System;
            [GenerateStructureHash]
            class ComplicatedMessage {
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
            class VersionMessage {
                [MemoryPackOrder(0)] public int Value1 { get; set; }
                [MemoryPackOrder(1)] public int Value2 { get; set; }
                [MemoryPackOrder(2)] public ValuesMessage Value3 { get; set; }
                [MemoryPackOrder(3)] public DateTime Value4 { get; set; }
            }
            [MemoryPackable(SerializeLayout.Explicit)]
            class ValuesMessage {
                [MemoryPackOrder(0)] public byte[] Value1 { get; set; }
                [MemoryPackOrder(1)] public ValueObjects Value2 { get; set; }
                [MemoryPackOrder(2)] public ValuesMessage Value3 { get; set; }
            }
            [MemoryPackable(SerializeLayout.Explicit)]
            class ValueObjects {
                [MemoryPackOrder(0)] public string[]? Values { get; set; }
            }
            public class GenerateStructureHashAttribute : Attribute
            {
                public int Hash { get; set; }
                public string[] Properties { get; set; }
            }
            """);
    }

    [Test]
    public void NullableEquality()
    {
        Run("""
            using System;
            [GenerateStructureHash(Hash = 391680852, Properties = [
            "Int32?[]", "Int32?[]", "Int32?[]?", "Int32?[]?", "Int32?", "Int32?"
            ])]
            class SimpleMessage {
                public Nullable<int>[] Value1;
                public int?[] Value2;
                public Nullable<int>[]? Value3;
                public int?[]? Value4;
                public Nullable<int> Value5;
                public int? Value6;
            }
            """);
    }
    
    [Test]
    public void ReorderEqualsSame()
    {
        Run("""
            using System;
            using MemoryPack;
            [GenerateStructureHash(Hash = -1449248132, Properties = ["Int16", "Int32", "Int64"])]
            class SimpleMessage {
                [MemoryPackOrder(2)] public long Value3 { get; set; }
                [MemoryPackOrder(1)] public int Value2 { get; set; }
                [MemoryPackOrder(0)] public short Value1 { get; set; }
            }

            [GenerateStructureHash(Hash = -1449248132, Properties = ["Int16", "Int32", "Int64"])]
            class SimpleMessage2 {
                [MemoryPackOrder(0)] public short Value1 { get; set; }
                [MemoryPackOrder(1)] public int Value2 { get; set; }
                [MemoryPackOrder(2)] public long Value3 { get; set; }
            }
            """);
    }
    
      [Test]
    public void ChangeInOrderChangesHash()
    {
        Run("""
            using System;
            [GenerateStructureHash(Hash = -1449248132, Properties = ["Int16", "Int32", "Int64"])]
            class SimpleMessage {
                [MemoryPackOrder(0)] public short Value1;
                [MemoryPackOrder(1)] public int Value2;
                [MemoryPackOrder(2)] public long Value3;
            }

            [GenerateStructureHash(Hash = -1705130407, Properties = ["Int64", "Int32", "Int16"])]
            class SimpleMessage2 {
                [MemoryPackOrder(0)] public long Value3;
                [MemoryPackOrder(1)] public int Value2;
                [MemoryPackOrder(2)] public short Value1;
            }
            """);
    }
    
    [Test]
    public void ChangeInOrderChangesHash_NoMemoryPackOrderAttribute()
    {
        Run("""
            using System;
            [GenerateStructureHash(Hash = -1449248132, Properties = ["Int16", "Int32", "Int64"])]
            class SimpleMessage {
                public short Value1;
                public int Value2;
                public long Value3;
            }

            [GenerateStructureHash(Hash = -1705130407, Properties = ["Int64", "Int32", "Int16"])]
            class SimpleMessage2 {
                public long Value3;
                public int Value2;
                public short Value1;
            }
            """);
    }
    
    [Test]
    public void PublicPropertiesAndPublicFieldsAreTreatedTheSame()
    {
        Run("""
            using System;
            [GenerateStructureHash(Hash = -1449248132, Properties = ["Int16", "Int32", "Int64"])]
            class SimpleMessage {
                public short Value1 { get; set; }
                public int Value2 { get; set; }
                public long Value3 { get; set; }
            }

            [GenerateStructureHash(Hash = -1449248132, Properties = ["Int16", "Int32", "Int64"])]
            class SimpleMessage2 {
                public short Value1
                public int Value2
                public long Value3
            }
            """);
    }

    [Test]
    public void IgnoresPrivatePropertiesAndFields()
    {
        Run("""
            using System;
            [GenerateStructureHash(Hash = -1449248132, Properties = ["Int16", "Int32", "Int64"])]
            class SimpleMessage {
                public short Value1 { get; set; }
                public int Value2 { get; set; }
                public long Value3 { get; set; }
                private long Value4 { get; set; }
            }

            [GenerateStructureHash(Hash = -1449248132, Properties = ["Int16", "Int32", "Int64"])]
            class SimpleMessage2 {
                public short Value1
                public int Value2
                public long Value3
                private long Value4
            }
            """);
    }

    [Test]
    public void ArityGenerics()
    {
        Run("""
            using System;
            [GenerateStructureHash(Hash = -2032222759, Properties = ["ValueTuple", "ValueTuple", "Tuple", "Tuple", "ValueTuple", "Int32", "Int32", "Tuple", "Int32", "Int32"])]
            class ComplicatedMessage {
                public ValueTuple<int> Value1;
                public ValueTuple<Tuple<int>> Value2;
                public Tuple<int> Value3;
                public Tuple<ValueTuple<int>> Value4;
            }
            """);
    }
    
    [Test]
    public void MemoryPackObjectInsideMemoryPackObject()
    {
        Run("""
            using System;
            [GenerateStructureHash(Hash = -2032222759, Properties = ["ValueTuple", "ValueTuple", "Tuple", "Tuple", "ValueTuple", "Int32", "Int32", "Tuple", "Int32", "Int32"])]
            class ComplicatedMessage {
                public VersionMessage 
            }
            [MemoryPackable(SerializeLayout.Explicit)]
            class VersionMessage {
                [MemoryPackOrder(0)] public int Value1;
                [MemoryPackOrder(1)] public int Value2;
                [MemoryPackOrder(2)] public ValuesMessage Value3;
                [MemoryPackOrder(3)] public DateTime Value4;
            }
            [MemoryPackable(SerializeLayout.Explicit)]
            class ValuesMessage {
                [MemoryPackOrder(0)] public byte[] Value1;
                [MemoryPackOrder(1)] public ValueObjects Value2;
                [MemoryPackOrder(2)] public ValuesMessage Value3;
            }
            [MemoryPackable(SerializeLayout.Explicit)]
            class ValueObjects {
                [MemoryPackOrder(0)] public string[]? Values;
            }
            """);
    }
    
    [Test]
    public void MemoryPackObjectHandlesSelfReferences()
    {
        Run("""
            using System;
            [GenerateStructureHash(Hash = 1428117199, Properties = ["Message", "Int32", "ValuesMessage", "Byte[]", "Message"])]
            class ComplicatedMessage {
                public Message Value1;
            }
            class Message {
                public int Value1;
                public ValuesMessage Value2;
            }
            class ValuesMessage {
                public byte[] Value1;
                public Message Value3;
            }
            """);
    }
    
    [Test]
    public void ChangeInPropertyObjectChangesHash()
    {
        Run("""
            using System;
            [GenerateStructureHash(Hash = -982755459, Properties = ["Message", "Int32"])]
            class ComplicatedMessage { public Message Value1; }
            class Message { public int Value1; }

            [GenerateStructureHash(Hash = 1046696349, Properties = ["Message2", "Int64"])]
            class ComplicatedMessage2 { public Message2 Value1; }
            class Message2 { public long Value1; }
            """);
    }    
    
    [Test]
    public void MemoryPackComplicatedValues()
    {
        Run("""
            using System;
            [GenerateStructureHash(Hash = -832585703, Properties = 
            ["List", "ValueTuple", "List", "Int32[]", "Int32[]?", "Dictionary", "String?", "String", "Byte", "Int32"])]
            class ComplicatedMessage {
                public List<ValueTuple<List<Dictionary<byte, int>, string?, string>, int[], int[]?>> Value1;
            }
            """);
    }

    private void Run(string code)
    {
        var diagnostic = CSharpGeneratorRunner.RunTypeWalkerGenerator(code, minDiagnostic: DiagnosticSeverity.Info);

        var first = diagnostic.Where(d => d.Id.StartsWith("TEST_FAIL")).ToArray();
        Assert.That(first, Is.Empty, string.Join('\n', first));
    }
}
