using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NexNet.Generator.Ir;
using TUnit.Assertions.Enums;

namespace NexNet.Generator.Tests.Ir;

/// <summary>
/// The multi-language opt-in (G02): [Nexus(Targets)], [assembly: NexusClientExport], [assembly: NexusTargetFormatter],
/// NEXNET043/NEXNET048, and the IR the real generator pipeline produces from them.
/// </summary>
public class IrOptInTests
{
    private const string Usings = """
        using System;
        using System.Threading.Tasks;
        using NexNet;
        using NexNet.Serialization;

        """;

    private sealed record OptInRun(NexusIr? Ir, Diagnostic[] Errors, Dictionary<string, string> Sources);

    /// <summary>Runs the real generator over the sources (each with a file path) and reads its IR step.</summary>
    private static OptInRun Run(params string[] sources)
    {
        var parseOptions = new CSharpParseOptions(LanguageVersion.CSharp13);
        var compilation = CSharpGeneratorRunner.CreateCompilation(sources[0]);
        compilation = compilation.ReplaceSyntaxTree(compilation.SyntaxTrees.Single(), compilation.SyntaxTrees.Single().WithFilePath("Source0.cs"));
        compilation = compilation.AddSyntaxTrees(sources.Skip(1).Select((s, i) => CSharpSyntaxTree.ParseText(s, parseOptions, path: $"Source{i + 1}.cs")));

        var (_, result, errors) = CSharpGeneratorRunner.RunGeneratorWithResult(compilation);
        if (result.Results.FirstOrDefault(r => r.Exception != null).Exception is { } exception)
            throw new InvalidOperationException("The generator threw.", exception);

        var steps = result.Results.Single().TrackedSteps;
        if (!steps.ContainsKey(NexusGenerator.IrTrackingName))
            throw new InvalidOperationException("The IR step did not run.");
        var ir = (NexusIr?)steps[NexusGenerator.IrTrackingName].Single().Outputs.Single().Value;

        // Only the generator's diagnostics: the fixture classes do not implement their interfaces.
        errors = errors.Where(d => d.Id.StartsWith("NEXNET", StringComparison.Ordinal)).ToArray();

        var generated = result.Results
            .SelectMany(r => r.GeneratedSources)
            .ToDictionary(s => s.HintName, s => s.SourceText.ToString());
        return new OptInRun(ir, errors, generated);
    }

    private static ExportIr Export(NexusIr? ir, string name)
    {
        if (ir is null)
            throw new InvalidOperationException("No IR was produced.");
        return ir.Exports.FirstOrDefault(e => e.Name == name) ?? throw new InvalidOperationException($"Export '{name}' not found.");
    }

    private static string[] Sorted(Dictionary<string, string> sources)
        => sources.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + "\n" + p.Value).ToArray();

    /// <summary>The JSON of an export without the fields that depend on how it was declared.</summary>
    private static string Shape(ExportIr export) => IrJson.WriteExport(export with { Name = "", Targets = 0, Docs = null });

    private static Diagnostic[] WithId(Diagnostic[] diagnostics, DiagnosticDescriptor descriptor)
        => diagnostics.Where(d => d.Id == descriptor.Id).ToArray();

    private const string Nexuses = Usings + """
        namespace Demo;

        public partial interface IClientNexus { ValueTask Notify(string message); }
        public partial interface IServerNexus { ValueTask<int> Add(int a, int b); }

        [Nexus<IClientNexus, IServerNexus>(NexusType = NexusType.Client)]
        public partial class ClientNexus { }

        [Nexus<IServerNexus, IClientNexus>(NexusType = NexusType.Server{TARGETS})]
        public partial class ServerNexus { }
        """;

    // ------------------------------------------------------------------ no targets

    [Test]
    public async Task NoTargets_ProducesNoIrAndTheSameCSharp()
    {
        var plain = Run(Nexuses.Replace("{TARGETS}", ""));
        await Assert.That(plain.Ir).IsNull();
        await Assert.That(plain.Errors).IsEmpty();

        // Opting in changes nothing in the C# output.
        var optedIn = Run(Nexuses.Replace("{TARGETS}", ", Targets = NexusTargets.Java"));
        await Assert.That(optedIn.Errors).IsEmpty();
        await Assert.That(Sorted(optedIn.Sources)).IsEquivalentTo(Sorted(plain.Sources), CollectionOrdering.Matching);
    }

    [Test]
    public async Task ExportWithTargetsNone_ProducesNoIr()
    {
        var run = Run(
            Nexuses.Replace("{TARGETS}", ", Targets = NexusTargets.None"),
            Usings + "[assembly: NexusClientExport<Demo.IServerNexus, Demo.IClientNexus>(Targets = NexusTargets.None)]\n");
        await Assert.That(run.Ir).IsNull();
        await Assert.That(run.Errors).IsEmpty();
    }

    [Test]
    public async Task TargetFormatterAlone_ProducesNoIr()
    {
        var run = Run(Nexuses.Replace("{TARGETS}", ""),
            Usings + "[assembly: NexusTargetFormatter<Uri>(NexusTargets.Java, \"com.example.UriFormatter\")]\n");
        await Assert.That(run.Ir).IsNull();
        await Assert.That(run.Errors).IsEmpty();
    }

    // ------------------------------------------------------------------ [Nexus(Targets)]

    [Test]
    public async Task ServerNexusWithTargets_IsExported()
    {
        var source = Nexuses.Replace("{TARGETS}", ", Targets = NexusTargets.Java | NexusTargets.JavaScript");
        var run = Run(source);
        await Assert.That(run.Errors).IsEmpty();
        await Assert.That(run.Ir!.IrVersion).IsEqualTo(NexusIr.CurrentVersion);

        // Only the opted-in server nexus is an export; the client nexus is not.
        await Assert.That(run.Ir.Exports.Select(e => e.Name).ToArray()).IsEquivalentTo(new[] { "ServerNexus" }, CollectionOrdering.Matching);
        var export = Export(run.Ir, "ServerNexus");
        await Assert.That(export.Targets).IsEqualTo(3);
        await Assert.That(export.ClrNamespace).IsEqualTo("Demo");
        await Assert.That(export.ServerApi.ClrName).IsEqualTo("Demo.IServerNexus");
        await Assert.That(export.ClientApi.ClrName).IsEqualTo("Demo.IClientNexus");

        // Identical to the G01 build of the same class.
        var g01 = IrTestRunner.Run(source).Ir.Exports.Single(e => e.Name == "ServerNexus");
        await Assert.That(IrJson.WriteExport(export)).IsEqualTo(IrJson.WriteExport(g01 with { Targets = 3 }));
    }

    [Test]
    public async Task IrIncludesNexusObjectAndAssemblyDeclaredTypes()
    {
        var run = Run(Usings + """
            [assembly: NexusSerializable<Demo.RuntimeOnly>]
            namespace Demo;

            [NexusObject] public class Unreferenced { [NexusKey(0)] public int Id { get; set; } }
            [NexusObject] public class RuntimeOnly { [NexusKey(0)] public string? Name { get; set; } }

            public partial interface IClientNexus { }
            public partial interface IServerNexus { ValueTask Ping(); }

            [Nexus<IServerNexus, IClientNexus>(NexusType = NexusType.Server, Targets = NexusTargets.JavaScript)]
            public partial class ServerNexus { }
            """);
        await Assert.That(run.Errors).IsEmpty();
        await Assert.That(run.Ir!.Types.Select(t => t.Id).ToArray()).IsEquivalentTo(new[] { "Demo.RuntimeOnly", "Demo.Unreferenced" });
    }

    // ------------------------------------------------------------------ [assembly: NexusClientExport]

    [Test]
    public async Task ClientExport_OfAVersionedV1Interface_MatchesTheCSharpClient()
    {
        var run = Run(IrFixtures.Versioned,
            Usings + """
                [assembly: NexusClientExport<NexNet.IntegrationTests.TestInterfaces.IVersionedServerNexusV1,
                    NexNet.IntegrationTests.TestInterfaces.ISimpleClientNexus>(Targets = NexusTargets.JavaScript)]
                """);
        await Assert.That(run.Errors).IsEmpty();

        // Default name: the server interface without its leading I. No [Nexus] class opted in.
        await Assert.That(run.Ir!.Exports.Select(e => e.Name).ToArray()).IsEquivalentTo(new[] { "VersionedServerNexusV1" }, CollectionOrdering.Matching);
        var export = Export(run.Ir, "VersionedServerNexusV1");
        await Assert.That(export.Targets).IsEqualTo(2);
        await Assert.That(export.ClrNamespace).IsEqualTo("NexNet.IntegrationTests.TestInterfaces");
        await Assert.That(export.Handshake.Version).IsEqualTo("v1.0");
        await Assert.That(export.Handshake.ServerNexusHash).IsEqualTo(-326045431);
        await Assert.That(export.Location).IsNotNull();

        // The same ids, hashes and handshake as the C# client nexus of that version.
        var csharpClient = IrTestRunner.Run(IrFixtures.Versioned).Ir.Exports.Single(e => e.Name == "VersionedClientNexusV1");
        await Assert.That(Shape(export)).IsEqualTo(Shape(csharpClient));
    }

    [Test]
    public async Task ClientExport_UsesTheGivenName_AndMergesWithClassExports()
    {
        var run = Run(
            Nexuses.Replace("{TARGETS}", ", Targets = NexusTargets.Java"),
            Usings + "[assembly: NexusClientExport<Demo.IServerNexus, Demo.IClientNexus>(Targets = NexusTargets.JavaScript, Name = \"AdderClient\")]\n");
        await Assert.That(run.Errors).IsEmpty();
        await Assert.That(run.Ir!.Exports.Select(e => e.Name).ToArray()).IsEquivalentTo(new[] { "AdderClient", "ServerNexus" }, CollectionOrdering.Matching);
        await Assert.That(Export(run.Ir, "AdderClient").Targets).IsEqualTo(2);
        await Assert.That(Export(run.Ir, "ServerNexus").Targets).IsEqualTo(1);

        // A class-less export does not know the server's [NexusAuthorize]; otherwise the APIs are the same.
        await Assert.That(Shape(Export(run.Ir, "AdderClient"))).IsEqualTo(Shape(Export(run.Ir, "ServerNexus")));
    }

    [Test]
    public async Task ClientExport_WithDuplicateMethodNames_ReportsNexnet042AndIsNotExported()
    {
        var run = Run(Usings + """
            [assembly: NexusClientExport<Demo.IServerApi, Demo.IClientApi>(Targets = NexusTargets.Java)]
            namespace Demo;
            public partial interface IClientApi { }
            public partial interface IServerApi { void Update(int a); void Update(string a); }
            """);
        var dup = WithId(run.Errors, DiagnosticDescriptors.DuplicateMethodName);
        await Assert.That(dup.Length).IsEqualTo(1);
        await Assert.That(dup[0].GetMessage()).Contains("'IServerApi'").And.Contains("'Update'");
        await Assert.That(run.Ir).IsNull();
    }

    // ------------------------------------------------------------------ [assembly: NexusTargetFormatter]

    private const string MoneyNexus = Usings + """
        [assembly: NexusFormatter<Demo.MoneyFormatter, Demo.Money>]
        {MAPPINGS}
        namespace Demo;

        public sealed class Money { public long Cents; }
        public sealed class MoneyFormatter : NexusFormatter<Money>
        {
            public override void Serialize(ref MsgPackWriter writer, Money? value) => writer.Write(value?.Cents ?? 0);
            public override void Deserialize(ref MsgPackReader reader, ref Money? value) => value = new Money { Cents = reader.ReadInt64() };
        }

        [NexusObject] public class Wallet { [NexusKey(0)] public Money[]? Coins { get; set; } }

        public partial interface IClientNexus { }
        public partial interface IServerNexus
        {
            ValueTask<Money> Convert(Money amount);
            ValueTask Store(Wallet wallet);
        }

        [Nexus<IServerNexus, IClientNexus>(NexusType = NexusType.Server, Targets = NexusTargets.Java | NexusTargets.JavaScript)]
        public partial class ServerNexus { }
        """;

    [Test]
    public async Task TargetFormatterMappings_LandOnExternalRefs()
    {
        var run = Run(MoneyNexus.Replace("{MAPPINGS}", """
            [assembly: NexusTargetFormatter<Demo.Money>(NexusTargets.JavaScript, "./money.js#MoneyFormatter")]
            [assembly: NexusTargetFormatter<Demo.Money>(NexusTargets.Java, "com.example.MoneyFormatter")]
            """));
        await Assert.That(run.Errors).IsEmpty();

        var expected = new[]
        {
            new NativeFormatterIr(1, "com.example.MoneyFormatter"),
            new NativeFormatterIr(2, "./money.js#MoneyFormatter"),
        };

        var convert = Export(run.Ir, "ServerNexus").ServerApi.Methods.Single(m => m.Name == "Convert");
        await Assert.That(convert.ReturnType!.Kind).IsEqualTo(TypeRefKind.External);
        await Assert.That(convert.ReturnType.Clr).IsEqualTo("Demo.Money");
        await Assert.That(convert.ReturnType.NativeFormatters.ToArray()).IsEquivalentTo(expected, CollectionOrdering.Matching);
        await Assert.That(convert.Params[0].Type!.NativeFormatters.ToArray()).IsEquivalentTo(expected, CollectionOrdering.Matching);

        // Nested references inside declarations are mapped too.
        var wallet = (ObjectDeclIr)run.Ir!.Types.Single(t => t.Id == "Demo.Wallet");
        var coins = wallet.Members.Single(m => m.Name == "Coins").Type;
        await Assert.That(coins.Kind).IsEqualTo(TypeRefKind.Seq);
        await Assert.That(coins.Args[0].NativeFormatters.ToArray()).IsEquivalentTo(expected, CollectionOrdering.Matching);

        // The mapping is part of the JSON and reads back.
        var json = IrJson.Write(run.Ir);
        await Assert.That(json).Contains("\"nativeFormatters\"");
        await Assert.That(IrJson.Write(IrJson.Read(json))).IsEqualTo(json);
    }

    [Test]
    public async Task TargetFormatterWithCombinedTargets_MapsEachTarget()
    {
        var run = Run(MoneyNexus.Replace("{MAPPINGS}",
            "[assembly: NexusTargetFormatter<Demo.Money>(NexusTargets.Java | NexusTargets.JavaScript, \"x.MoneyFormatter\")]"));
        await Assert.That(run.Errors).IsEmpty();
        var convert = Export(run.Ir, "ServerNexus").ServerApi.Methods.Single(m => m.Name == "Convert");
        await Assert.That(convert.ReturnType!.NativeFormatters.ToArray()).IsEquivalentTo(new[]
        {
            new NativeFormatterIr(1, "x.MoneyFormatter"),
            new NativeFormatterIr(2, "x.MoneyFormatter"),
        }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task WithoutTargetFormatter_ExternalRefHasNoNativeFormatters()
    {
        var run = Run(MoneyNexus.Replace("{MAPPINGS}", ""));
        await Assert.That(run.Errors).IsEmpty();
        var convert = Export(run.Ir, "ServerNexus").ServerApi.Methods.Single(m => m.Name == "Convert");
        await Assert.That(convert.ReturnType!.Kind).IsEqualTo(TypeRefKind.External);
        await Assert.That(convert.ReturnType.NativeFormatters).IsEmpty();
        await Assert.That(IrJson.Write(run.Ir!)).DoesNotContain("nativeFormatters");
    }

    // ------------------------------------------------------------------ diagnostics

    [Test]
    public async Task TargetsOnClientNexus_ReportsNexnet043()
    {
        var run = Run(Usings + """
            namespace Demo;
            public partial interface IClientNexus { }
            public partial interface IServerNexus { }

            [Nexus<IClientNexus, IServerNexus>(NexusType = NexusType.Client, Targets = NexusTargets.Java)]
            public partial class ClientNexus { }
            """);

        var errors = WithId(run.Errors, DiagnosticDescriptors.TargetsOnClientNexus);
        await Assert.That(errors.Length).IsEqualTo(1);
        await Assert.That(errors[0].Severity).IsEqualTo(DiagnosticSeverity.Error);
        await Assert.That(errors[0].GetMessage()).Contains("'ClientNexus'");
        await Assert.That(errors[0].Location.GetLineSpan().Path).IsEqualTo("Source0.cs");
        await Assert.That(errors[0].Location.GetLineSpan().StartLinePosition.Line).IsEqualTo(8);

        // A client nexus is never an export.
        await Assert.That(run.Ir).IsNull();
    }

    [Test]
    public async Task InvalidTargetFormatters_ReportNexnet048()
    {
        var run = Run(MoneyNexus.Replace("{MAPPINGS}", """
            [assembly: NexusTargetFormatter<Demo.Money>(NexusTargets.Java, "com.example.MoneyFormatter")]
            [assembly: NexusTargetFormatter<Demo.Money>(NexusTargets.Java, "com.example.Other")]
            [assembly: NexusTargetFormatter<Demo.Money>(NexusTargets.None, "x.Formatter")]
            [assembly: NexusTargetFormatter<Demo.Money>(NexusTargets.JavaScript, " ")]
            [assembly: NexusTargetFormatter<Demo.Money>((NexusTargets)4, "x.Formatter")]
            """));

        var errors = WithId(run.Errors, DiagnosticDescriptors.InvalidTargetFormatter);
        await Assert.That(errors.Select(e => e.GetMessage()).ToArray()).IsEquivalentTo(new[]
        {
            "The [NexusTargetFormatter] for 'Demo.Money' is invalid: a formatter for Java is already declared",
            "The [NexusTargetFormatter] for 'Demo.Money' is invalid: the target is NexusTargets.None",
            "The [NexusTargetFormatter] for 'Demo.Money' is invalid: the native formatter name is empty",
            "The [NexusTargetFormatter] for 'Demo.Money' is invalid: the target 4 is not a defined NexusTargets value",
        });
        await Assert.That(errors.All(e => e.Severity == DiagnosticSeverity.Error)).IsTrue();
        await Assert.That(errors.All(e => e.Location.GetLineSpan().Path == "Source0.cs")).IsTrue();
        await Assert.That(errors.Select(e => e.Location.GetLineSpan().StartLinePosition.Line).ToArray()).IsEquivalentTo(new[] { 6, 7, 8, 9 });

        // The valid first mapping still applies.
        var convert = Export(run.Ir, "ServerNexus").ServerApi.Methods.Single(m => m.Name == "Convert");
        await Assert.That(convert.ReturnType!.NativeFormatters.ToArray()).IsEquivalentTo(new[] { new NativeFormatterIr(1, "com.example.MoneyFormatter") }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task DefaultExportName()
    {
        await Assert.That(Extraction.TargetExtractor.DefaultExportName("IChatServer")).IsEqualTo("ChatServer");
        await Assert.That(Extraction.TargetExtractor.DefaultExportName("Inventory")).IsEqualTo("Inventory");
        await Assert.That(Extraction.TargetExtractor.DefaultExportName("I")).IsEqualTo("I");
    }
}
