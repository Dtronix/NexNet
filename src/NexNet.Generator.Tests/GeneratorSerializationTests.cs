using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using NUnit.Framework;

namespace NexNet.Generator.Tests;

/// <summary>
/// MessagePack backend: [NexusObject] formatter generation and serialization diagnostics.
/// Every test compiles the generated code, so emitted formatter errors surface as diagnostics.
/// </summary>
class GeneratorSerializationTests
{
    private static Diagnostic[] Run(string types, string serverMethods, string implementation, DiagnosticSeverity min = DiagnosticSeverity.Error)
    {
        return CSharpGeneratorRunner.RunGenerator($$"""
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NexNet;
using NexNet.Collections;
using NexNet.Pipes;
using NexNet.Serialization;
namespace NexNetDemo;
{{types}}
partial interface IClientNexus { }
partial interface IServerNexus { {{serverMethods}} }
[Nexus<IClientNexus, IServerNexus>(NexusType = NexusType.Client)]
partial class ClientNexus : IClientNexus { }
[Nexus<IServerNexus, IClientNexus>(NexusType = NexusType.Server)]
partial class ServerNexus : IServerNexus { {{implementation}} }
""", minDiagnostic: min, options: SerializerBackendOptions.MessagePack);
    }

    [Test]
    public void SimpleObjectGenerates()
    {
        var diagnostics = Run("""
[NexusObject]
public class Person { [NexusKey(0)] public int Id { get; set; } [NexusKey(1)] public string? Name { get; set; } }
""", "ValueTask<Person> Get(Person p);", "public ValueTask<Person> Get(Person p) => new(p);");
        Assert.That(diagnostics, Is.Empty);
    }

    [Test]
    public void NestedObjectsCollectionsAndTuplesGenerate()
    {
        var diagnostics = Run("""
[NexusObject]
public class Inner { [NexusKey(0)] public double[]? Scores { get; set; } [NexusKey(1)] public List<string>? Tags { get; set; } }
[NexusObject]
public class Outer
{
    [NexusKey(0)] public Inner? Inner { get; set; }
    [NexusKey(1)] public Dictionary<string, Inner>? Map { get; set; }
    [NexusKey(2)] public Inner[]? Array { get; set; }
    [NexusKey(3)] public Guid Id { get; set; }
    [NexusKey(5)] public DayOfWeek Day { get; set; }
    [NexusKey(6)] public int? Optional { get; set; }
}
""", "void Update(Outer data, List<ValueTuple<Tuple<Outer, int>>> data2, IReadOnlyList<Inner> list);",
            "public void Update(Outer data, List<ValueTuple<Tuple<Outer, int>>> data2, IReadOnlyList<Inner> list) { }");
        Assert.That(diagnostics, Is.Empty);
    }

    [Test]
    public void StructRecordAndConstructorTypesGenerate()
    {
        var diagnostics = Run("""
[NexusObject]
public struct Point { [NexusKey(0)] public int X { get; set; } [NexusKey(1)] public int Y; }
[NexusObject]
public record Pair([property: NexusKey(0)] int A, [property: NexusKey(1)] string B);
[NexusObject]
public class Immutable
{
    public Immutable(int value, string text) { Value = value; Text = text; }
    [NexusKey(0)] public int Value { get; }
    [NexusKey(1)] public string Text { get; }
}
[NexusObject]
public class WithInit { [NexusKey(0)] public required int Id { get; init; } [NexusKey(1)] public string? Name { get; init; } }
""", "void Update(Point p, Pair pair, Immutable i, WithInit w);", "public void Update(Point p, Pair pair, Immutable i, WithInit w) { }");
        Assert.That(diagnostics, Is.Empty);
    }

    [Test]
    public void PrivateMembersUseUnsafeAccessor()
    {
        var diagnostics = Run("""
[NexusObject]
public class Secret
{
    [NexusKey(0)] private int _hidden;
    [NexusKey(1)] public string? Name { get; private set; }
    [NexusKey(2)] internal int Internal { get; set; }
    public int Hidden => _hidden;
}
""", "void Update(Secret s);", "public void Update(Secret s) { }");
        Assert.That(diagnostics, Is.Empty);
    }

    [Test]
    public void UnionGenerates()
    {
        var diagnostics = Run("""
[NexusObject]
[NexusUnion<Circle>(0)]
[NexusUnion<Square>(1)]
public interface IShape { }
[NexusObject]
public class Circle : IShape { [NexusKey(0)] public double Radius { get; set; } }
[NexusObject]
public class Square : IShape { [NexusKey(0)] public double Side { get; set; } }
""", "ValueTask<IShape> Echo(IShape shape);", "public ValueTask<IShape> Echo(IShape shape) => new(shape);");
        Assert.That(diagnostics, Is.Empty);
    }

    [Test]
    public void GenericObjectGenerates()
    {
        var diagnostics = Run("""
[NexusObject]
public class Envelope<T> { [NexusKey(0)] public T? Value { get; set; } [NexusKey(1)] public List<T>? Items { get; set; } }
[NexusObject]
public class Person { [NexusKey(0)] public int Id { get; set; } }
""", "void Update(Envelope<Person> e, Envelope<int> i);", "public void Update(Envelope<Person> e, Envelope<int> i) { }");
        Assert.That(diagnostics, Is.Empty);
    }

    [Test]
    public void ChannelAndCollectionTypesGenerate()
    {
        var diagnostics = Run("""
[NexusObject]
public class Item { [NexusKey(0)] public int Id { get; set; } }
""", "ValueTask Stream(INexusDuplexChannel<Item> channel); [NexusCollection(NexusCollectionMode.ServerToClient)] NexNet.Collections.Lists.INexusList<Item> Items { get; }",
            "public ValueTask Stream(INexusDuplexChannel<Item> channel) => default;");
        Assert.That(diagnostics, Is.Empty);
    }

    [Test]
    public void AssemblyDeclarationsGenerate()
    {
        var diagnostics = CSharpGeneratorRunner.RunGenerator("""
using System.Collections.Generic;
using NexNet.Serialization;
[assembly: NexusSerializable<List<NexNetDemo.Item>>]
[assembly: NexusFormatter<NexNetDemo.ThirdPartyFormatter, NexNetDemo.ThirdParty>]
namespace NexNetDemo;
[NexusObject]
public class Item { [NexusKey(0)] public int Id { get; set; } }
public class ThirdParty { public int Value; }
public sealed class ThirdPartyFormatter : NexusFormatter<ThirdParty>
{
    public override void Serialize(ref MsgPackWriter writer, ThirdParty? value) => writer.Write(value?.Value ?? 0);
    public override void Deserialize(ref MsgPackReader reader, ref ThirdParty? value) => value = new ThirdParty { Value = reader.ReadInt32() };
}
""", options: SerializerBackendOptions.MessagePack);
        Assert.That(diagnostics, Is.Empty);
    }

    [Test]
    public void UserFormatterTypeIsAcceptedAsParameter()
    {
        var diagnostics = CSharpGeneratorRunner.RunGenerator("""
using System.Threading.Tasks;
using NexNet;
using NexNet.Serialization;
[assembly: NexusFormatter<NexNetDemo.ThirdPartyFormatter, NexNetDemo.ThirdParty>]
namespace NexNetDemo;
public class ThirdParty { public int Value; }
public sealed class ThirdPartyFormatter : NexusFormatter<ThirdParty>
{
    public override void Serialize(ref MsgPackWriter writer, ThirdParty? value) => writer.Write(value?.Value ?? 0);
    public override void Deserialize(ref MsgPackReader reader, ref ThirdParty? value) => value = new ThirdParty { Value = reader.ReadInt32() };
}
partial interface IClientNexus { }
partial interface IServerNexus { void Update(ThirdParty value); }
[Nexus<IClientNexus, IServerNexus>(NexusType = NexusType.Client)]
partial class ClientNexus : IClientNexus { }
[Nexus<IServerNexus, IClientNexus>(NexusType = NexusType.Server)]
partial class ServerNexus : IServerNexus { public void Update(ThirdParty value) { } }
""", options: SerializerBackendOptions.MessagePack);
        Assert.That(diagnostics, Is.Empty);
    }

    // ------------------------------------------------------------------ Diagnostics

    [Test]
    public void UnannotatedTypeReportsNEXNET028()
    {
        var diagnostics = Run("public class Plain { public int Id { get; set; } }", "void Update(Plain p);", "public void Update(Plain p) { }");
        Assert.That(diagnostics.Any(d => d.Id == "NEXNET028"), Is.True);
    }

    [Test]
    public void MemoryPackOnlyTypeIsAcceptedByMemoryPackBackend()
    {
        var diagnostics = CSharpGeneratorRunner.RunGenerator("""
using NexNet;
using MemoryPack;
namespace NexNetDemo;
[MemoryPackable]
public partial class Plain { public int Id { get; set; } }
partial interface IClientNexus { }
partial interface IServerNexus { void Update(Plain p); }
[Nexus<IClientNexus, IServerNexus>(NexusType = NexusType.Client)]
partial class ClientNexus : IClientNexus { }
[Nexus<IServerNexus, IClientNexus>(NexusType = NexusType.Server)]
partial class ServerNexus : IServerNexus { public void Update(Plain p) { } }
""", options: SerializerBackendOptions.MemoryPack);
        Assert.That(diagnostics, Is.Empty);
    }

    [Test]
    public void DuplicateKeyReportsNEXNET029()
    {
        var diagnostics = Run("[NexusObject] public class D { [NexusKey(0)] public int A { get; set; } [NexusKey(0)] public int B { get; set; } }",
            "void Update(D d);", "public void Update(D d) { }");
        Assert.That(diagnostics.Any(d => d.Id == "NEXNET029"), Is.True);
    }

    [Test]
    public void MissingKeyReportsNEXNET030()
    {
        var diagnostics = Run("[NexusObject] public class D { [NexusKey(0)] public int A { get; set; } public int B { get; set; } }",
            "void Update(D d);", "public void Update(D d) { }");
        Assert.That(diagnostics.Any(d => d.Id == "NEXNET030"), Is.True);
    }

    [Test]
    public void IgnoredAndComputedMembersDoNotNeedKeys()
    {
        var diagnostics = Run("[NexusObject] public class D { [NexusKey(0)] public int A { get; set; } [NexusIgnore] public int B { get; set; } public int C => A * 2; }",
            "void Update(D d);", "public void Update(D d) { }");
        Assert.That(diagnostics, Is.Empty);
    }

    [Test]
    public void UnassignableMemberReportsNEXNET031()
    {
        var diagnostics = Run("""
[NexusObject]
public class D { public D() { } [NexusKey(0)] public int Computed => 5; }
""", "void Update(D d);", "public void Update(D d) { }");
        Assert.That(diagnostics.Any(d => d.Id == "NEXNET031"), Is.True);
    }

    [Test]
    public void AmbiguousConstructorReportsNEXNET032()
    {
        var diagnostics = Run("""
[NexusObject]
public class D { public D(int a) { A = a; } public D(int a, int b) { A = a; } [NexusKey(0)] public int A { get; } }
""", "void Update(D d);", "public void Update(D d) { }");
        Assert.That(diagnostics.Any(d => d.Id == "NEXNET032"), Is.True);
    }

    [Test]
    public void DuplicateUnionTagReportsNEXNET033()
    {
        var diagnostics = Run("""
[NexusObject]
[NexusUnion<A>(0)]
[NexusUnion<B>(0)]
public interface IU { }
[NexusObject] public class A : IU { [NexusKey(0)] public int X { get; set; } }
[NexusObject] public class B : IU { [NexusKey(0)] public int X { get; set; } }
""", "void Update(IU u);", "public void Update(IU u) { }");
        Assert.That(diagnostics.Any(d => d.Id == "NEXNET033"), Is.True);
    }

    [Test]
    public void AbstractWithoutUnionReportsNEXNET034()
    {
        var diagnostics = Run("[NexusObject] public abstract class Base { [NexusKey(0)] public int X { get; set; } }",
            "void Update(Base b);", "public void Update(Base b) { }");
        Assert.That(diagnostics.Any(d => d.Id == "NEXNET034"), Is.True);
    }

    [Test]
    public void LargeKeyGapReportsNEXNET037Warning()
    {
        var diagnostics = Run("[NexusObject] public class D { [NexusKey(0)] public int A { get; set; } [NexusKey(40)] public int B { get; set; } }",
            "void Update(D d);", "public void Update(D d) { }", DiagnosticSeverity.Warning);
        Assert.That(diagnostics.Any(d => d.Id == "NEXNET037" && d.Severity == DiagnosticSeverity.Warning), Is.True);
        Assert.That(diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error), Is.Empty);
    }

    // ------------------------------------------------------------------ Channel analyzer (NEXNET038)

    private static Diagnostic[] RunChannelAnalyzer(string source, SerializerBackendOptions backend)
    {
        var compilation = CSharpGeneratorRunner.CreateCompilation(source);
        var analyzers = System.Collections.Immutable.ImmutableArray.Create<Microsoft.CodeAnalysis.Diagnostics.DiagnosticAnalyzer>(
            new NexNet.Generator.Serialization.ChannelTypeAnalyzer());
        var options = new Microsoft.CodeAnalysis.Diagnostics.AnalyzerOptions(
            System.Collections.Immutable.ImmutableArray<AdditionalText>.Empty, backend);
        return compilation.WithAnalyzers(analyzers, options).GetAnalyzerDiagnosticsAsync().GetAwaiter().GetResult().ToArray();
    }

    private const string ChannelSource = """
using System.Collections.Generic;
using System.Threading.Tasks;
using NexNet.Pipes;
using NexNet.Serialization;
[assembly: NexusSerializable<List<Declared.Item>>]
namespace Declared;
[NexusObject] public class Item { [NexusKey(0)] public int Id { get; set; } }
public class Plain { public int Id { get; set; } }
public static class Usage
{
    public static async Task Run(INexusDuplexPipe pipe)
    {
        await pipe.GetChannelReader<int>();
        await pipe.GetChannelReader<string[]>();
        await pipe.GetChannelReader<Item>();
        await pipe.GetChannelReader<List<Item>>();
        await pipe.GetChannelWriter<Plain>();
        await pipe.GetChannelWriter<Item[]>();
    }
}
""";

    [Test]
    public void ChannelAnalyzerReportsUnregisteredTypes()
    {
        var diagnostics = RunChannelAnalyzer(ChannelSource, SerializerBackendOptions.MessagePack);
        var reported = diagnostics.Where(d => d.Id == "NEXNET038").Select(d => d.GetMessage()).ToArray();
        Assert.That(reported.Length, Is.EqualTo(2), string.Join("; ", reported));
        Assert.That(reported.Any(m => m.Contains("Plain")), Is.True);
        Assert.That(reported.Any(m => m.Contains("Item[]")), Is.True);
    }

    [Test]
    public void ChannelAnalyzerIsInactiveForMemoryPackBackend()
    {
        var diagnostics = RunChannelAnalyzer(ChannelSource, SerializerBackendOptions.MemoryPack);
        Assert.That(diagnostics.Where(d => d.Id == "NEXNET038"), Is.Empty);
    }

    // ------------------------------------------------------------------ Hashing

    [Test]
    public void NexusKeyOrderAffectsHash()
    {
        var compilation1 = CSharpGeneratorRunner.CreateCompilation("""
using NexNet.Serialization;
[NexusObject] public class A { [NexusKey(0)] public int X { get; set; } [NexusKey(1)] public string? Y { get; set; } }
""");
        var compilation2 = CSharpGeneratorRunner.CreateCompilation("""
using NexNet.Serialization;
[NexusObject] public class A { [NexusKey(1)] public int X { get; set; } [NexusKey(0)] public string? Y { get; set; } }
""");

        var h1 = new TypeHasher(generateWalkString: true).GetHashResult(compilation1.GetTypeByMetadataName("A")!);
        var h2 = new TypeHasher(generateWalkString: true).GetHashResult(compilation2.GetTypeByMetadataName("A")!);
        Assert.That(h1.Hash, Is.Not.EqualTo(h2.Hash));
        Assert.That(h1.WalkString, Does.Contain("[NexusObject]"));
        Assert.That(h1.WalkString, Does.Contain("[Key:1]"));
    }
}
