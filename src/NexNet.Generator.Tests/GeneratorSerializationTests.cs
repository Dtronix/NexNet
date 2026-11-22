using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using NexNet.Generator.Serialization;
using TUnit.Assertions.Enums;

namespace NexNet.Generator.Tests;

/// <summary>
/// [NexusObject] formatter generation and serialization diagnostics.
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
""", minDiagnostic: min);
    }

    [Test]
    public async Task SimpleObjectGenerates()
    {
        var diagnostics = Run("""
[NexusObject]
public class Person { [NexusKey(0)] public int Id { get; set; } [NexusKey(1)] public string? Name { get; set; } }
""", "ValueTask<Person> Get(Person p);", "public ValueTask<Person> Get(Person p) => new(p);");
        await Assert.That(diagnostics).IsEmpty();
    }

    [Test]
    public async Task NestedObjectsCollectionsAndTuplesGenerate()
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
        await Assert.That(diagnostics).IsEmpty();
    }

    [Test]
    public async Task StructRecordAndConstructorTypesGenerate()
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
        await Assert.That(diagnostics).IsEmpty();
    }

    [Test]
    public async Task PrivateMembersUseUnsafeAccessor()
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
        await Assert.That(diagnostics).IsEmpty();
    }

    [Test]
    public async Task UnionGenerates()
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
        await Assert.That(diagnostics).IsEmpty();
    }

    [Test]
    public async Task GenericObjectGenerates()
    {
        var diagnostics = Run("""
[NexusObject]
public class Envelope<T> { [NexusKey(0)] public T? Value { get; set; } [NexusKey(1)] public List<T>? Items { get; set; } }
[NexusObject]
public class Person { [NexusKey(0)] public int Id { get; set; } }
""", "void Update(Envelope<Person> e, Envelope<int> i);", "public void Update(Envelope<Person> e, Envelope<int> i) { }");
        await Assert.That(diagnostics).IsEmpty();
    }

    [Test]
    public async Task ChannelAndCollectionTypesGenerate()
    {
        var diagnostics = Run("""
[NexusObject]
public class Item { [NexusKey(0)] public int Id { get; set; } }
""", "ValueTask Stream(INexusDuplexChannel<Item> channel); [NexusCollection(NexusCollectionMode.ServerToClient)] NexNet.Collections.Lists.INexusList<Item> Items { get; }",
            "public ValueTask Stream(INexusDuplexChannel<Item> channel) => default;");
        await Assert.That(diagnostics).IsEmpty();
    }

    [Test]
    public async Task AssemblyDeclarationsGenerate()
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
""");
        await Assert.That(diagnostics).IsEmpty();
    }

    [Test]
    public async Task UserFormatterTypeIsAcceptedAsParameter()
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
""");
        await Assert.That(diagnostics).IsEmpty();
    }

    // ------------------------------------------------------------------ Diagnostics

    [Test]
    public async Task UnannotatedTypeReportsNEXNET028()
    {
        var diagnostics = Run("public class Plain { public int Id { get; set; } }", "void Update(Plain p);", "public void Update(Plain p) { }");
        await Assert.That(diagnostics.Any(d => d.Id == "NEXNET028")).IsTrue();
    }

    [Test]
    public async Task DuplicateKeyReportsNEXNET029()
    {
        var diagnostics = Run("[NexusObject] public class D { [NexusKey(0)] public int A { get; set; } [NexusKey(0)] public int B { get; set; } }",
            "void Update(D d);", "public void Update(D d) { }");
        await Assert.That(diagnostics.Any(d => d.Id == "NEXNET029")).IsTrue();
    }

    [Test]
    public async Task MissingKeyReportsNEXNET030()
    {
        var diagnostics = Run("[NexusObject] public class D { [NexusKey(0)] public int A { get; set; } public int B { get; set; } }",
            "void Update(D d);", "public void Update(D d) { }");
        await Assert.That(diagnostics.Any(d => d.Id == "NEXNET030")).IsTrue();
    }

    [Test]
    public async Task IgnoredAndComputedMembersDoNotNeedKeys()
    {
        var diagnostics = Run("[NexusObject] public class D { [NexusKey(0)] public int A { get; set; } [NexusIgnore] public int B { get; set; } public int C => A * 2; }",
            "void Update(D d);", "public void Update(D d) { }");
        await Assert.That(diagnostics).IsEmpty();
    }

    [Test]
    public async Task UnassignableMemberReportsNEXNET031()
    {
        var diagnostics = Run("""
[NexusObject]
public class D { public D() { } [NexusKey(0)] public int Computed => 5; }
""", "void Update(D d);", "public void Update(D d) { }");
        await Assert.That(diagnostics.Any(d => d.Id == "NEXNET031")).IsTrue();
    }

    [Test]
    public async Task AmbiguousConstructorReportsNEXNET032()
    {
        var diagnostics = Run("""
[NexusObject]
public class D { public D(int a) { A = a; } public D(int a, int b) { A = a; } [NexusKey(0)] public int A { get; } }
""", "void Update(D d);", "public void Update(D d) { }");
        await Assert.That(diagnostics.Any(d => d.Id == "NEXNET032")).IsTrue();
    }

    [Test]
    public async Task DuplicateUnionTagReportsNEXNET033()
    {
        var diagnostics = Run("""
[NexusObject]
[NexusUnion<A>(0)]
[NexusUnion<B>(0)]
public interface IU { }
[NexusObject] public class A : IU { [NexusKey(0)] public int X { get; set; } }
[NexusObject] public class B : IU { [NexusKey(0)] public int X { get; set; } }
""", "void Update(IU u);", "public void Update(IU u) { }");
        await Assert.That(diagnostics.Any(d => d.Id == "NEXNET033")).IsTrue();
    }

    [Test]
    public async Task AbstractWithoutUnionReportsNEXNET034()
    {
        var diagnostics = Run("[NexusObject] public abstract class Base { [NexusKey(0)] public int X { get; set; } }",
            "void Update(Base b);", "public void Update(Base b) { }");
        await Assert.That(diagnostics.Any(d => d.Id == "NEXNET034")).IsTrue();
    }

    [Test]
    public async Task LargeKeyGapReportsNEXNET037Warning()
    {
        var diagnostics = Run("[NexusObject] public class D { [NexusKey(0)] public int A { get; set; } [NexusKey(40)] public int B { get; set; } }",
            "void Update(D d);", "public void Update(D d) { }", DiagnosticSeverity.Warning);
        await Assert.That(diagnostics.Any(d => d.Id == "NEXNET037" && d.Severity == DiagnosticSeverity.Warning)).IsTrue();
        await Assert.That(diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)).IsEmpty();
    }

    // ------------------------------------------------------------------ Channel analyzer (NEXNET038)

    private static Diagnostic[] RunChannelAnalyzer(string source)
    {
        var compilation = CSharpGeneratorRunner.CreateCompilation(source);
        var analyzers = System.Collections.Immutable.ImmutableArray.Create<Microsoft.CodeAnalysis.Diagnostics.DiagnosticAnalyzer>(
            new NexNet.Generator.Serialization.ChannelTypeAnalyzer());
        var options = new Microsoft.CodeAnalysis.Diagnostics.AnalyzerOptions(
            System.Collections.Immutable.ImmutableArray<AdditionalText>.Empty);
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
    public async Task ChannelAnalyzerReportsUnregisteredTypes()
    {
        var diagnostics = RunChannelAnalyzer(ChannelSource);
        var reported = diagnostics.Where(d => d.Id == "NEXNET038").Select(d => d.GetMessage()).ToArray();
        await Assert.That(reported.Length).IsEqualTo(2).Because(string.Join("; ", reported));
        await Assert.That(reported.Any(m => m.Contains("Plain"))).IsTrue();
        await Assert.That(reported.Any(m => m.Contains("Item[]"))).IsTrue();
    }

    // ------------------------------------------------------------------ Hashing

    [Test]
    public async Task NexusKeyOrderAffectsHash()
    {
        var compilation1 = CSharpGeneratorRunner.CreateCompilation("""
using NexNet.Serialization;
[NexusObject] public class A { [NexusKey(0)] public int X { get; set; } [NexusKey(1)] public string? Y { get; set; } }
""");
        var compilation2 = CSharpGeneratorRunner.CreateCompilation("""
using NexNet.Serialization;
[NexusObject] public class A { [NexusKey(1)] public int X { get; set; } [NexusKey(0)] public string? Y { get; set; } }
""");

        var h1 = ShapeHasher.HashWithListing(new ShapeBuilder(compilation1).Get(compilation1.GetTypeByMetadataName("A")!));
        var h2 = ShapeHasher.HashWithListing(new ShapeBuilder(compilation2).Get(compilation2.GetTypeByMetadataName("A")!));
        await Assert.That(h1.Hash).IsNotEqualTo(h2.Hash);
        await Assert.That(h1.Listing).Contains("#0 object A");
        await Assert.That(h1.Listing).Contains("0: ");
    }

    // ------------------------------------------------------------------ Formatter output (one file per assembly)

    private const string SharedTypeSource = """
using System.Collections.Generic;
using System.Threading.Tasks;
using NexNet;
using NexNet.Serialization;
namespace NexNetDemo;
[NexusObject]
public class Person { [NexusKey(0)] public int Id { get; set; } [NexusKey(1)] public string? Name { get; set; } [NexusKey(2)] public Address? Home { get; set; } }
[NexusObject]
public class Address { [NexusKey(0)] public string? City { get; set; } }
public partial interface IClientNexus { ValueTask<Person> GetPerson(); }
public partial interface IServerNexus { void Update(Person p); ValueTask<List<Person>> All(); }
public partial interface IOtherClientNexus { }
public partial interface IOtherServerNexus { void Store(Person p, Address a); }
[Nexus<IClientNexus, IServerNexus>(NexusType = NexusType.Client)]
public partial class ClientNexus : IClientNexus { public ValueTask<Person> GetPerson() => new(new Person()); }
[Nexus<IServerNexus, IClientNexus>(NexusType = NexusType.Server)]
public partial class ServerNexus : IServerNexus { public void Update(Person p) { } public ValueTask<List<Person>> All() => new(new List<Person>()); }
[Nexus<IOtherServerNexus, IOtherClientNexus>(NexusType = NexusType.Server)]
public partial class OtherServerNexus : IOtherServerNexus { public void Store(Person p, Address a) { } }
""";

    [Test]
    public async Task TypeReachedFromSeveralProducersHasOneFormatter()
    {
        // Person is reached from two nexus pairs and is also declared locally: one formatter class, one registration.
        var (_, result, errors) = CSharpGeneratorRunner.RunGeneratorWithResult(CSharpGeneratorRunner.CreateCompilation(SharedTypeSource));
        await Assert.That(errors).IsEmpty().Because(string.Join("\n", errors.Select(e => e.ToString())));

        var sources = result.Results.Single().GeneratedSources;
        var formatterFiles = sources.Where(s => s.HintName == NexusGenerator.FormattersFileName).ToArray();
        await Assert.That(formatterFiles.Length).IsEqualTo(1);

        var code = formatterFiles[0].SourceText.ToString();
        await Assert.That(System.Text.RegularExpressions.Regex.Matches(code, @"file sealed class __NexusFormatter_Person_").Count).IsEqualTo(1);
        await Assert.That(System.Text.RegularExpressions.Regex.Matches(code, @"file sealed class __NexusFormatter_Address_").Count).IsEqualTo(1);
        await Assert.That(System.Text.RegularExpressions.Regex.Matches(code, @"Register<global::NexNetDemo\.Person>\(").Count).IsEqualTo(1);
        await Assert.That(System.Text.RegularExpressions.Regex.Matches(code, @"ModuleInitializer").Count).IsEqualTo(1);

        // Nexus files hold only nexus code and argument readers.
        foreach (var nexusFile in sources.Where(s => s.HintName != NexusGenerator.FormattersFileName))
            await Assert.That(nexusFile.SourceText.ToString()).DoesNotContain("__NexusFormatter_").Because(nexusFile.HintName);
    }

    [Test]
    public async Task GeneratedSourcesAreByteIdenticalAcrossRuns()
    {
        var compilation = CSharpGeneratorRunner.CreateCompilation(SharedTypeSource);
        var first = CSharpGeneratorRunner.RunGeneratorWithResult(compilation).Result.Results.Single().GeneratedSources;
        var second = CSharpGeneratorRunner.RunGeneratorWithResult(compilation).Result.Results.Single().GeneratedSources;

        await Assert.That(second.Select(s => s.HintName).ToArray()).IsEquivalentTo(first.Select(s => s.HintName).ToArray(), CollectionOrdering.Matching);
        for (var i = 0; i < first.Length; i++)
            await Assert.That(second[i].SourceText.ToString()).IsEqualTo(first[i].SourceText.ToString()).Because(first[i].HintName);
    }

    [Test]
    public async Task UnrelatedEditDoesNotRegenerateFormatters()
    {
        var compilation = CSharpGeneratorRunner.CreateCompilation(SharedTypeSource);
        var (driver, _, _) = CSharpGeneratorRunner.RunGeneratorWithResult(compilation);

        var edited = compilation.AddSyntaxTrees(Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(
            "namespace Unrelated; public static class Helper { public static int Twice(int x) => x * 2; }",
            new Microsoft.CodeAnalysis.CSharp.CSharpParseOptions(Microsoft.CodeAnalysis.CSharp.LanguageVersion.CSharp13)));
        var (_, result, _) = CSharpGeneratorRunner.RunGeneratorWithResult(edited, driver);

        var steps = result.Results.Single().TrackedSteps[NexusGenerator.FormattersTrackingName];
        var reasons = steps.SelectMany(s => s.Outputs).Select(o => o.Reason).ToArray();
        await Assert.That(reasons).IsNotEmpty();
        await Assert.That(reasons.All(r => r is IncrementalStepRunReason.Unchanged or IncrementalStepRunReason.Cached)).IsTrue().Because(string.Join(", ", reasons));
    }
}
