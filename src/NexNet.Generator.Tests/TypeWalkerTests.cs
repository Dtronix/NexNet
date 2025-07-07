using Microsoft.CodeAnalysis;
using NUnit.Framework;

namespace NexNet.Generator.Tests;

public class TypeWalkerTests
{
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
            [GenerateStructureHash(Hash = 1365541633, Properties = ["ValueTuple", "ValueTuple", "Tuple", "Tuple", "ValueTuple", "Int32", "Int32", "Tuple"])]
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
            [GenerateStructureHash(Hash = -1711761833, Properties = ["VersionMessage", "Int32", "Int32", "ValuesMessage", "DateTime", "Byte[]", "ValueObjects", "ValuesMessage", "String[]?"])]
            class ComplicatedMessage {
                public VersionMessage Value1;
            }
            class VersionMessage {
                public int Value1;
                public int Value2;
                public ValuesMessage Value3;
                public DateTime Value4;
            }
            class ValuesMessage {
                public byte[] Value1;
                public ValueObjects Value2;
                public ValuesMessage Value3;
            }
            class ValueObjects {
                public string[]? Values;
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
    public void MemoryPackObjectHandlesSelfReferencesMultipleTimes()
    {
        Run("""
            using System;
            [GenerateStructureHash(Hash = 844793965, 
            Properties = ["Message", "ComplicatedMessage", "ValuesMessage", "Message", "Int32", "Int32", "Int32", "ValuesMessage", "Byte[]", "Message"])]
            class ComplicatedMessage {
                public Message Value1;
                public ComplicatedMessage Value2;
                public ValuesMessage Value3;
                public Message Value3;
            }
            class Message {
                public int Value1;
                public int Value2;
                public int Value3;
                public ValuesMessage Value4;
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
    
    [Test]
    public void Structs()
    {
        Run("""
            using System;
            [GenerateStructureHash(Hash = -982755459, Properties = ["Message", "Int32"])]
            class ComplicatedMessage { public Message Value1; }
            struct Message { public int Value1; }

            [GenerateStructureHash(Hash = 1046696349, Properties = ["Message2", "Int64"])]
            struct ComplicatedMessage2 { public Message2 Value1; }
            struct Message2 { public long Value1; }
            """);
    }    
    
    [Test]
    public void HandlesAllMemoryPackableTypes()
    {
        Run("""
            using System;
            using System.Buffers;
            using System.Collections;
            using System.Collections.Concurrent;
            using System.Collections.Immutable;
            using System.Collections.ObjectModel;
            using System.Globalization;
            using System.Numerics;
            using System.Text;
            [GenerateStructureHash(Hash = 1893215763, Properties = [
                "String", "Decimal", "Half", "Int128", "UInt128", "Guid", "Rune", "BigInteger", 
                "TimeSpan", "DateTime", "DateTimeOffset", "TimeOnly", "DateOnly", "TimeZoneInfo", 
                "Complex", "Plane", "Quaternion", "Matrix3x2", "Matrix4x4", "Vector2", "Vector3",
                "Vector4", "Uri", "Version", "StringBuilder", "Type", "BitArray", "CultureInfo", 
                "Int32[]", "Int32[,]", "Int32[,,]", "Int32[,,,]"])]
            class ComplicatedMessage {
                public string Value1;
                public decimal Value2;
                public Half Value3;
                public Int128 Value4;
                public UInt128 Value5;
                public Guid Value6;
                public Rune Value7;
                public BigInteger Value8;
                public TimeSpan Value9;
                public DateTime Value10;
                public DateTimeOffset Value11;
                public TimeOnly Value12;
                public DateOnly Value13;
                public TimeZoneInfo Value14;
                public Complex Value15;
                public Plane Value16;
                public Quaternion Value17;
                public Matrix3x2 Value18;
                public Matrix4x4 Value19;
                public Vector2 Value20;
                public Vector3 Value21;
                public Vector4 Value22;
                public Uri Value23;
                public Version Value24;
                public StringBuilder Value25;
                public Type Value26;
                public BitArray Value27;
                public CultureInfo Value28;
                public int[] Value29;
                public int[,] Value30;
                public int[,,] Value31;
                public int[,,,] Value32;
            }
            """);
    }    
    
[Test]
    public void HandlesAllMemoryPackableTypes_Generics()
    {
        Run("""
            using System;
            using System.Buffers;
            using System.Collections;
            using System.Collections.Concurrent;
            using System.Collections.Immutable;
            using System.Collections.ObjectModel;
            using System.Globalization;
            using System.Numerics;
            using System.Text;
            [GenerateStructureHash(Hash = 2046228362, Properties = [
                "Memory", "ReadOnlyMemory", "ArraySegment", "ReadOnlySequence", "Byte?", "Lazy",
                "KeyValuePair", "Tuple", "Tuple", "Tuple", "Tuple", "ValueTuple", "ValueTuple",
                "ValueTuple", "ValueTuple", "List", "LinkedList", "Queue", "Stack", "HashSet",
                "SortedSet", "PriorityQueue", "Dictionary", "SortedList", "SortedDictionary", 
                "ReadOnlyDictionary", "Collection", "ReadOnlyCollection", "ObservableCollection",
                "ReadOnlyObservableCollection", "IEnumerable", "ICollection", "IList", "IReadOnlyCollection", 
                "IReadOnlyList", "ISet", "IDictionary", "IReadOnlyDictionary", "ILookup", "IGrouping", "ConcurrentBag",
                "ConcurrentQueue", "ConcurrentStack", "ConcurrentDictionary", "BlockingCollection", "ImmutableList",
                "IImmutableList", "Int32", "Int32", "Int32", "Int32", "Int64", "Int32", "Int32", "Int32", "Int32", 
                "Int64", "Int32", "Int64", "Int32", "Int64", "Int32", "Int64", "Int32", "Int32", "Int32", "Int32",
                "Int32", "Int32", "Int32", "Int32", "Int32", "Int32", "Int64", "Int32", "Int64", "Int32", "Int64",
                "Int32", "Int64", "Int32", "Int64", "Int32", "Int32", "Int32", "Int32", "Int32", "Int32", "Int32",
                "Byte", "Int16", "Int32", "Int64", "Byte", "Int16", "Int32", "Byte", "Int16", "Byte", "Byte", 
                "Int16", "Int32", "Int64", "Byte", "Int16", "Int32", "Byte", "Int16", "Byte", "Byte", "Int32", 
                "Byte", "Byte", "Byte", "Byte", "Byte"])]
            class ComplicatedMessage {
                public Memory<byte> Value33;
                public ReadOnlyMemory<byte> Value34;
                public ArraySegment<byte> Value35;
                public ReadOnlySequence<byte> Value36;
                public Nullable<byte> Value37;
                public Lazy<byte> Value38;
                public KeyValuePair<byte, int> Value39;
                public Tuple<byte> Value40;
                public Tuple<byte, short> Value41;
                public Tuple<byte, short, int> Value42;
                public Tuple<byte, short, int, long> Value43;
                public ValueTuple<byte> Value44;
                public ValueTuple<byte, short> Value45;
                public ValueTuple<byte, short, int> Value46;
                public ValueTuple<byte, short, int, long> Value47;
                public List<int> Value48;
                public LinkedList<int> Value49;
                public Queue<int> Value50;
                public Stack<int> Value51;
                public HashSet<int> Value52;
                public SortedSet<int> Value53;
                public PriorityQueue<long, int> Value55;
                public Dictionary<long, int> Value56;
                public SortedList<long, int> Value57;
                public SortedDictionary<long, int> Value58;
                public ReadOnlyDictionary<long, int> Value59;
                public Collection<int> Value60;
                public ReadOnlyCollection<int> Value61;
                public ObservableCollection<int> Value62;
                public ReadOnlyObservableCollection<int> Value63;
                public IEnumerable<int> Value64;
                public ICollection<int> Value65;
                public IList<int> Value66;
                public IReadOnlyCollection<int> Value67;
                public IReadOnlyList<int> Value68;
                public ISet<int> Value69;
                public IDictionary<int, long> Value70;
                public IReadOnlyDictionary<int, long> Value71;
                public ILookup<int, long> Value72;
                public IGrouping<int, long> Value73;
                public ConcurrentBag<int> Value74;
                public ConcurrentQueue<int> Value75;
                public ConcurrentStack<int> Value76;
                public ConcurrentDictionary<int, long> Value77;
                public BlockingCollection<int> Value78;
                public ImmutableList<int> Value79;
                public IImmutableList<int> Value80;
            }
            """);
    }    
    
    [Test]
    public void WillNotWalkArrayType()
    {
        Run("""
            using System;
            [GenerateStructureHash(Hash = 1543671865, 
            Properties = ["Byte[]"])]
            class ComplicatedMessage {
                public byte[] Value1;
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
