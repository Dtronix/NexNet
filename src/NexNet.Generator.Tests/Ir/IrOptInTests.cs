using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NexNet.Generator.Ir;
using NUnit.Framework;

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
        Assert.That(result.Results.Select(r => r.Exception), Is.All.Null, "The generator threw.");

        var steps = result.Results.Single().TrackedSteps;
        Assert.That(steps.ContainsKey(NexusGenerator.IrTrackingName), Is.True, "The IR step did not run.");
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
        Assert.That(ir, Is.Not.Null, "No IR was produced.");
        return ir!.Exports.FirstOrDefault(e => e.Name == name) ?? throw new AssertionException($"Export '{name}' not found.");
    }

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
    public void NoTargets_ProducesNoIrAndTheSameCSharp()
    {
        var plain = Run(Nexuses.Replace("{TARGETS}", ""));
        Assert.That(plain.Ir, Is.Null);
        Assert.That(plain.Errors, Is.Empty);

        // Opting in changes nothing in the C# output.
        var optedIn = Run(Nexuses.Replace("{TARGETS}", ", Targets = NexusTargets.Java"));
        Assert.That(optedIn.Errors, Is.Empty);
        Assert.That(optedIn.Sources, Is.EquivalentTo(plain.Sources));
    }

    [Test]
    public void ExportWithTargetsNone_ProducesNoIr()
    {
        var run = Run(
            Nexuses.Replace("{TARGETS}", ", Targets = NexusTargets.None"),
            Usings + "[assembly: NexusClientExport<Demo.IServerNexus, Demo.IClientNexus>(Targets = NexusTargets.None)]\n");
        Assert.That(run.Ir, Is.Null);
        Assert.That(run.Errors, Is.Empty);
    }

    [Test]
    public void TargetFormatterAlone_ProducesNoIr()
    {
        var run = Run(Nexuses.Replace("{TARGETS}", ""),
            Usings + "[assembly: NexusTargetFormatter<Uri>(NexusTargets.Java, \"com.example.UriFormatter\")]\n");
        Assert.That(run.Ir, Is.Null);
        Assert.That(run.Errors, Is.Empty);
    }

    // ------------------------------------------------------------------ [Nexus(Targets)]

    [Test]
    public void ServerNexusWithTargets_IsExported()
    {
        var source = Nexuses.Replace("{TARGETS}", ", Targets = NexusTargets.Java | NexusTargets.JavaScript");
        var run = Run(source);
        Assert.That(run.Errors, Is.Empty);
        Assert.That(run.Ir!.IrVersion, Is.EqualTo(NexusIr.CurrentVersion));

        // Only the opted-in server nexus is an export; the client nexus is not.
        Assert.That(run.Ir.Exports.Select(e => e.Name), Is.EqualTo(new[] { "ServerNexus" }));
        var export = Export(run.Ir, "ServerNexus");
        Assert.That(export.Targets, Is.EqualTo(3));
        Assert.That(export.ClrNamespace, Is.EqualTo("Demo"));
        Assert.That(export.ServerApi.ClrName, Is.EqualTo("Demo.IServerNexus"));
        Assert.That(export.ClientApi.ClrName, Is.EqualTo("Demo.IClientNexus"));

        // Identical to the G01 build of the same class.
        var g01 = IrTestRunner.Run(source).Ir.Exports.Single(e => e.Name == "ServerNexus");
        Assert.That(IrJson.WriteExport(export), Is.EqualTo(IrJson.WriteExport(g01 with { Targets = 3 })));
    }

    [Test]
    public void IrIncludesNexusObjectAndAssemblyDeclaredTypes()
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
        Assert.That(run.Errors, Is.Empty);
        Assert.That(run.Ir!.Types.Select(t => t.Id), Is.EquivalentTo(new[] { "Demo.RuntimeOnly", "Demo.Unreferenced" }));
    }

    // ------------------------------------------------------------------ [assembly: NexusClientExport]

    [Test]
    public void ClientExport_OfAVersionedV1Interface_MatchesTheCSharpClient()
    {
        var run = Run(IrFixtures.Versioned,
            Usings + """
                [assembly: NexusClientExport<NexNet.IntegrationTests.TestInterfaces.IVersionedServerNexusV1,
                    NexNet.IntegrationTests.TestInterfaces.ISimpleClientNexus>(Targets = NexusTargets.JavaScript)]
                """);
        Assert.That(run.Errors, Is.Empty);

        // Default name: the server interface without its leading I. No [Nexus] class opted in.
        Assert.That(run.Ir!.Exports.Select(e => e.Name), Is.EqualTo(new[] { "VersionedServerNexusV1" }));
        var export = Export(run.Ir, "VersionedServerNexusV1");
        Assert.That(export.Targets, Is.EqualTo(2));
        Assert.That(export.ClrNamespace, Is.EqualTo("NexNet.IntegrationTests.TestInterfaces"));
        Assert.That(export.Handshake.Version, Is.EqualTo("v1.0"));
        Assert.That(export.Handshake.ServerNexusHash, Is.EqualTo(-326045431));
        Assert.That(export.Location, Is.Not.Null);

        // The same ids, hashes and handshake as the C# client nexus of that version.
        var csharpClient = IrTestRunner.Run(IrFixtures.Versioned).Ir.Exports.Single(e => e.Name == "VersionedClientNexusV1");
        Assert.That(Shape(export), Is.EqualTo(Shape(csharpClient)));
    }

    [Test]
    public void ClientExport_UsesTheGivenName_AndMergesWithClassExports()
    {
        var run = Run(
            Nexuses.Replace("{TARGETS}", ", Targets = NexusTargets.Java"),
            Usings + "[assembly: NexusClientExport<Demo.IServerNexus, Demo.IClientNexus>(Targets = NexusTargets.JavaScript, Name = \"AdderClient\")]\n");
        Assert.That(run.Errors, Is.Empty);
        Assert.That(run.Ir!.Exports.Select(e => e.Name), Is.EqualTo(new[] { "AdderClient", "ServerNexus" }));
        Assert.That(Export(run.Ir, "AdderClient").Targets, Is.EqualTo(2));
        Assert.That(Export(run.Ir, "ServerNexus").Targets, Is.EqualTo(1));

        // A class-less export does not know the server's [NexusAuthorize]; otherwise the APIs are the same.
        Assert.That(Shape(Export(run.Ir, "AdderClient")), Is.EqualTo(Shape(Export(run.Ir, "ServerNexus"))));
    }

    [Test]
    public void ClientExport_WithDuplicateMethodNames_ReportsNexnet042AndIsNotExported()
    {
        var run = Run(Usings + """
            [assembly: NexusClientExport<Demo.IServerApi, Demo.IClientApi>(Targets = NexusTargets.Java)]
            namespace Demo;
            public partial interface IClientApi { }
            public partial interface IServerApi { void Update(int a); void Update(string a); }
            """);
        var dup = WithId(run.Errors, DiagnosticDescriptors.DuplicateMethodName);
        Assert.That(dup, Has.Length.EqualTo(1));
        Assert.That(dup[0].GetMessage(), Does.Contain("'IServerApi'").And.Contain("'Update'"));
        Assert.That(run.Ir, Is.Null);
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
    public void TargetFormatterMappings_LandOnExternalRefs()
    {
        var run = Run(MoneyNexus.Replace("{MAPPINGS}", """
            [assembly: NexusTargetFormatter<Demo.Money>(NexusTargets.JavaScript, "./money.js#MoneyFormatter")]
            [assembly: NexusTargetFormatter<Demo.Money>(NexusTargets.Java, "com.example.MoneyFormatter")]
            """));
        Assert.That(run.Errors, Is.Empty);

        var expected = new[]
        {
            new NativeFormatterIr(1, "com.example.MoneyFormatter"),
            new NativeFormatterIr(2, "./money.js#MoneyFormatter"),
        };

        var convert = Export(run.Ir, "ServerNexus").ServerApi.Methods.Single(m => m.Name == "Convert");
        Assert.That(convert.ReturnType!.Kind, Is.EqualTo(TypeRefKind.External));
        Assert.That(convert.ReturnType.Clr, Is.EqualTo("Demo.Money"));
        Assert.That(convert.ReturnType.NativeFormatters, Is.EqualTo(expected));
        Assert.That(convert.Params[0].Type!.NativeFormatters, Is.EqualTo(expected));

        // Nested references inside declarations are mapped too.
        var wallet = (ObjectDeclIr)run.Ir!.Types.Single(t => t.Id == "Demo.Wallet");
        var coins = wallet.Members.Single(m => m.Name == "Coins").Type;
        Assert.That(coins.Kind, Is.EqualTo(TypeRefKind.Seq));
        Assert.That(coins.Args[0].NativeFormatters, Is.EqualTo(expected));

        // The mapping is part of the JSON and reads back.
        var json = IrJson.Write(run.Ir);
        Assert.That(json, Does.Contain("\"nativeFormatters\""));
        Assert.That(IrJson.Write(IrJson.Read(json)), Is.EqualTo(json));
    }

    [Test]
    public void TargetFormatterWithCombinedTargets_MapsEachTarget()
    {
        var run = Run(MoneyNexus.Replace("{MAPPINGS}",
            "[assembly: NexusTargetFormatter<Demo.Money>(NexusTargets.Java | NexusTargets.JavaScript, \"x.MoneyFormatter\")]"));
        Assert.That(run.Errors, Is.Empty);
        var convert = Export(run.Ir, "ServerNexus").ServerApi.Methods.Single(m => m.Name == "Convert");
        Assert.That(convert.ReturnType!.NativeFormatters, Is.EqualTo(new[]
        {
            new NativeFormatterIr(1, "x.MoneyFormatter"),
            new NativeFormatterIr(2, "x.MoneyFormatter"),
        }));
    }

    [Test]
    public void WithoutTargetFormatter_ExternalRefHasNoNativeFormatters()
    {
        var run = Run(MoneyNexus.Replace("{MAPPINGS}", ""));
        Assert.That(run.Errors, Is.Empty);
        var convert = Export(run.Ir, "ServerNexus").ServerApi.Methods.Single(m => m.Name == "Convert");
        Assert.That(convert.ReturnType!.Kind, Is.EqualTo(TypeRefKind.External));
        Assert.That(convert.ReturnType.NativeFormatters, Is.Empty);
        Assert.That(IrJson.Write(run.Ir!), Does.Not.Contain("nativeFormatters"));
    }

    // ------------------------------------------------------------------ diagnostics

    [Test]
    public void TargetsOnClientNexus_ReportsNexnet043()
    {
        var run = Run(Usings + """
            namespace Demo;
            public partial interface IClientNexus { }
            public partial interface IServerNexus { }

            [Nexus<IClientNexus, IServerNexus>(NexusType = NexusType.Client, Targets = NexusTargets.Java)]
            public partial class ClientNexus { }
            """);

        var errors = WithId(run.Errors, DiagnosticDescriptors.TargetsOnClientNexus);
        Assert.That(errors, Has.Length.EqualTo(1));
        Assert.That(errors[0].Severity, Is.EqualTo(DiagnosticSeverity.Error));
        Assert.That(errors[0].GetMessage(), Does.Contain("'ClientNexus'"));
        Assert.That(errors[0].Location.GetLineSpan().Path, Is.EqualTo("Source0.cs"));
        Assert.That(errors[0].Location.GetLineSpan().StartLinePosition.Line, Is.EqualTo(8));

        // A client nexus is never an export.
        Assert.That(run.Ir, Is.Null);
    }

    [Test]
    public void InvalidTargetFormatters_ReportNexnet048()
    {
        var run = Run(MoneyNexus.Replace("{MAPPINGS}", """
            [assembly: NexusTargetFormatter<Demo.Money>(NexusTargets.Java, "com.example.MoneyFormatter")]
            [assembly: NexusTargetFormatter<Demo.Money>(NexusTargets.Java, "com.example.Other")]
            [assembly: NexusTargetFormatter<Demo.Money>(NexusTargets.None, "x.Formatter")]
            [assembly: NexusTargetFormatter<Demo.Money>(NexusTargets.JavaScript, " ")]
            [assembly: NexusTargetFormatter<Demo.Money>((NexusTargets)4, "x.Formatter")]
            """));

        var errors = WithId(run.Errors, DiagnosticDescriptors.InvalidTargetFormatter);
        Assert.That(errors.Select(e => e.GetMessage()), Is.EquivalentTo(new[]
        {
            "The [NexusTargetFormatter] for 'Demo.Money' is invalid: a formatter for Java is already declared",
            "The [NexusTargetFormatter] for 'Demo.Money' is invalid: the target is NexusTargets.None",
            "The [NexusTargetFormatter] for 'Demo.Money' is invalid: the native formatter name is empty",
            "The [NexusTargetFormatter] for 'Demo.Money' is invalid: the target 4 is not a defined NexusTargets value",
        }));
        Assert.That(errors.Select(e => e.Severity), Is.All.EqualTo(DiagnosticSeverity.Error));
        Assert.That(errors.Select(e => e.Location.GetLineSpan().Path), Is.All.EqualTo("Source0.cs"));
        Assert.That(errors.Select(e => e.Location.GetLineSpan().StartLinePosition.Line), Is.EquivalentTo(new[] { 6, 7, 8, 9 }));

        // The valid first mapping still applies.
        var convert = Export(run.Ir, "ServerNexus").ServerApi.Methods.Single(m => m.Name == "Convert");
        Assert.That(convert.ReturnType!.NativeFormatters, Is.EqualTo(new[] { new NativeFormatterIr(1, "com.example.MoneyFormatter") }));
    }

    [Test]
    public void DefaultExportName()
    {
        Assert.That(Extraction.TargetExtractor.DefaultExportName("IChatServer"), Is.EqualTo("ChatServer"));
        Assert.That(Extraction.TargetExtractor.DefaultExportName("Inventory"), Is.EqualTo("Inventory"));
        Assert.That(Extraction.TargetExtractor.DefaultExportName("I"), Is.EqualTo("I"));
    }
}
