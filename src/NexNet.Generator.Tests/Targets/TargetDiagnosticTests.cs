using Microsoft.CodeAnalysis;
using NexNet.Generator.Ir;
using NexNet.Generator.Serialization;
using NexNet.Generator.Targets;
using NexNet.Generator.Targets.Java;
using NexNet.Generator.Validation;
using static NexNet.Generator.Tests.Targets.TargetTestRunner;

namespace NexNet.Generator.Tests.Targets;

/// <summary>The target validator (G03): one test group per diagnostic, NEXNET040-047, through the real generator.</summary>
public class TargetDiagnosticTests
{
    private const string Nexus = Usings + """
        namespace Demo;

        {TYPES}

        public partial interface IClientNexus { ValueTask Notify(string message); }
        public partial interface IServerNexus
        {
            {SERVER}
        }

        [Nexus<IClientNexus, IServerNexus>(NexusType = NexusType.Client)]
        public partial class ClientNexus { }

        [Nexus<IServerNexus, IClientNexus>(NexusType = NexusType.Server, Targets = {TARGETS})]
        public partial class ServerNexus { }
        """;

    private const string Both = "NexusTargets.Java | NexusTargets.JavaScript";

    private static string Source(string server, string targets = Both, string types = "")
        => Nexus.Replace("{SERVER}", server).Replace("{TARGETS}", targets).Replace("{TYPES}", types);

    private static TargetRun RunBoth(params string[] sources) => Run(BothConfigured(), sources);

    // ------------------------------------------------------------------ clean

    [Test]
    public async Task SupportedNexus_ReportsNothing()
    {
        var run = RunBoth(Source("""
            ValueTask<int> Add(int a, int b);
            ValueTask<Dictionary<string, List<long>>> Lookup(HashSet<Guid> ids, DateTime? since);
            """));

        await Assert.That(run.Ir).IsNotNull();
        await Assert.That(run.Diagnostics).IsEmpty();
    }

    [Test]
    public async Task NoTargets_ReportsNothingEvenWhenConfigured()
    {
        var run = RunBoth(Source("ValueTask<int> Add(int a, int b);").Replace(", Targets = " + Both, ""));
        await Assert.That(run.Ir).IsNull();
        await Assert.That(run.Diagnostics).IsEmpty();
    }

    // ------------------------------------------------------------------ NEXNET040

    private const string MoneyTypes = """
        public sealed class Money { public long Cents; }
        public sealed class MoneyFormatter : NexusFormatter<Money>
        {
            public override void Serialize(ref MsgPackWriter writer, Money? value) => writer.Write(value?.Cents ?? 0);
            public override void Deserialize(ref MsgPackReader reader, ref Money? value) => value = new Money { Cents = reader.ReadInt64() };
        }

        [NexusObject] public class Wallet { [NexusKey(0)] public Money[]? Coins { get; set; } }
        """;

    private const string MoneyServer = """
        ValueTask<Money> Convert(Money amount);
        ValueTask Store(Wallet wallet);
        """;

    private static string MoneySource(string mappings)
        => Usings + "[assembly: NexusFormatter<Demo.MoneyFormatter, Demo.Money>]\n" + mappings + "\n"
           + Source(MoneyServer, types: MoneyTypes).Substring(Usings.Length);

    [Test]
    public async Task UserFormatterWithoutNativeFormatter_Is040ForEachUnmappedTarget()
    {
        var run = RunBoth(MoneySource("""[assembly: NexusTargetFormatter<Demo.Money>(NexusTargets.Java, "com.example.MoneyFormatter")]"""));

        var messages = run.Messages("NEXNET040");
        await Assert.That(messages).IsEquivalentTo(new[]
        {
            "The type 'Demo.Money' used by the return type of 'Demo.IServerNexus.Convert' cannot be represented in JavaScript: it is serialized by a custom formatter; declare [assembly: NexusTargetFormatter<Demo.Money>(NexusTargets.JavaScript, \"<fully qualified native formatter>\")]",
            "The type 'Demo.Money' used by parameter 'amount' of 'Demo.IServerNexus.Convert' cannot be represented in JavaScript: it is serialized by a custom formatter; declare [assembly: NexusTargetFormatter<Demo.Money>(NexusTargets.JavaScript, \"<fully qualified native formatter>\")]",
            "The type 'Array<Demo.Money>' used by member 'Coins' of 'Demo.Wallet' cannot be represented in JavaScript: it is serialized by a custom formatter; declare [assembly: NexusTargetFormatter<Demo.Money>(NexusTargets.JavaScript, \"<fully qualified native formatter>\")]",
        });
        await Assert.That(run.WithId("NEXNET040").All(d => d.Severity == DiagnosticSeverity.Error)).IsTrue();

        // Reported at the method, not at Location.None.
        var convert = run.WithId("NEXNET040")[0];
        await Assert.That(convert.Location.GetLineSpan().Path).IsEqualTo("Source0.cs");
    }

    [Test]
    public async Task UserFormatterMappedForEveryTarget_IsNot040()
    {
        var run = RunBoth(MoneySource("""
            [assembly: NexusTargetFormatter<Demo.Money>(NexusTargets.Java, "com.example.MoneyFormatter")]
            [assembly: NexusTargetFormatter<Demo.Money>(NexusTargets.JavaScript, "./money.js#MoneyFormatter")]
            """));
        await Assert.That(run.Diagnostics).IsEmpty();
    }

    // ------------------------------------------------------------------ NEXNET041

    [Test]
    public async Task MethodsThatDifferOnlyInCase_Are041InEveryTarget()
    {
        var run = RunBoth(Source("""
            ValueTask Add(int a);
            ValueTask add(int a);
            """));

        await Assert.That(run.Messages("NEXNET041")).IsEquivalentTo(new[]
        {
            "Name collision (Java): method 'Add' and method 'add' of 'Demo.IServerNexus' both map to 'add'",
            "Name collision (JavaScript): method 'Add' and method 'add' of 'Demo.IServerNexus' both map to 'add'",
        });
    }

    [Test]
    public async Task ParametersAndMembersThatCollideAfterMapping_Are041()
    {
        var run = Run(new Dictionary<string, string> { ["build_property.NexNetTargets"] = "javascript" }, Source(
            "ValueTask Move(int _x, int x, Pos pos);",
            "NexusTargets.JavaScript",
            "[NexusObject] public class Pos { [NexusKey(0)] public int Value; [NexusKey(1)] public int value; }"));

        await Assert.That(run.Messages("NEXNET041")).IsEquivalentTo(new[]
        {
            "Name collision (JavaScript): parameter '_x' and parameter 'x' of 'Demo.IServerNexus.Move' both map to 'x'",
            "Name collision (JavaScript): member 'Value' and member 'value' of 'Demo.Pos' both map to 'value'",
        });
    }

    [Test]
    public async Task KeywordNamesAreEscaped_NotCollisions()
    {
        var run = RunBoth(Source("ValueTask Default(int @class, int function, int @default);"));
        await Assert.That(run.Diagnostics).IsEmpty();
    }

    [Test]
    public async Task ClassExportAndClientExportWithTheSameName_Are041()
    {
        var run = RunBoth(
            Source("ValueTask<int> Add(int a, int b);", "NexusTargets.Java"),
            Usings + "[assembly: NexusClientExport<Demo.IServerNexus, Demo.IClientNexus>(Targets = NexusTargets.JavaScript)]\n");

        var collisions = run.WithId("NEXNET041");
        await Assert.That(collisions.Length).IsEqualTo(2);
        await Assert.That(collisions.Select(d => d.Location.GetLineSpan().Path).Distinct().Count()).IsEqualTo(2);
        await Assert.That(collisions[0].GetMessage(System.Globalization.CultureInfo.InvariantCulture))
            .StartsWith("Name collision (exports): 'Demo.ServerNexus' is exported more than once");
    }

    // ------------------------------------------------------------------ NEXNET042

    [Test]
    public async Task DuplicateMethodNamesInTheIr_Are042()
    {
        // G02 never builds such an export (NEXNET042 blocks it), so the check is exercised on a hand-edited IR.
        var run = RunBoth(Source("ValueTask<int> Add(int a, int b);"));
        var ir = run.Ir!;
        var export = ir.Exports[0];
        var add = export.ServerApi.Methods[0];
        var duplicated = export with
        {
            ServerApi = export.ServerApi with
            {
                Methods = new EquatableArray<MethodIr>(new[] { add, add with { Id = (ushort)(add.Id + 1) } }),
            },
        };
        var edited = ir with { Exports = new EquatableArray<ExportIr>(new[] { duplicated }) };

        var options = new TargetOptions("java", true, new EquatableArray<TargetOption>(new[] { new TargetOption("Package", "com.acme") }));
        var diagnostics = JavaTarget.Instance.Validate(edited, duplicated, options).ToArray();

        await Assert.That(diagnostics.Select(d => (d.Id, d.Arg0, d.Arg1)).ToArray())
            .IsEquivalentTo(new[] { ("NEXNET042", "Demo.IServerNexus", "Add") });
    }

    // ------------------------------------------------------------------ NEXNET044

    [Test]
    public async Task OptedInTargetWithoutOutputDir_Is044WarningAndNothingElse()
    {
        // JavaScript is configured; Java is not, so its unmapped Money type is not checked.
        var run = Run(
            new Dictionary<string, string> { ["build_property.NexNetTargets"] = "javascript" },
            MoneySource("""[assembly: NexusTargetFormatter<Demo.Money>(NexusTargets.JavaScript, "./money.js#MoneyFormatter")]"""));

        await Assert.That(run.Diagnostics.Select(d => d.Id).ToArray()).IsEquivalentTo(new[] { "NEXNET044" });
        var warning = run.Diagnostics.Single();
        await Assert.That(warning.Severity).IsEqualTo(DiagnosticSeverity.Warning);
        await Assert.That(warning.GetMessage(System.Globalization.CultureInfo.InvariantCulture)).IsEqualTo(
            "'Demo.ServerNexus' opts in to the Java target, but no output directory is configured for it, so no Java code is generated. Set NexNetJavaOutputDir or add a NexNetTarget item with an OutputDir.");
        await Assert.That(warning.Location.GetLineSpan().Path).IsEqualTo("Source0.cs");
    }

    [Test]
    public async Task NoConfigurationAtAll_Is044PerTarget()
    {
        var run = Run(null, Source("ValueTask<int> Add(int a, int b);"));
        await Assert.That(run.Messages("NEXNET044").Length).IsEqualTo(2);
        await Assert.That(run.Diagnostics.All(d => d.Id == "NEXNET044")).IsTrue();
    }

    // ------------------------------------------------------------------ NEXNET045

    [Test]
    public async Task UnknownConfiguredTarget_Is045()
    {
        var options = BothConfigured();
        options["build_property.NexNetTargets"] = "java; Rust ;javascript";
        var run = Run(options, Source("ValueTask<int> Add(int a, int b);"));

        await Assert.That(run.Messages("NEXNET045")).IsEquivalentTo(new[]
        {
            "Invalid NexNet target configuration: unknown target 'rust' in NexNetTargets (known: java, javascript)",
        });
    }

    [Test]
    public async Task JavaWithoutPackage_Is045_EvenWithoutExports()
    {
        var run = Run(new Dictionary<string, string> { ["build_property.NexNetTargets"] = "java" }, Usings);
        await Assert.That(run.Messages("NEXNET045")).IsEquivalentTo(new[]
        {
            "Invalid NexNet target configuration: the java target requires a package: set NexNetJavaPackage or <NexNetTarget Include=\"java\" Package=\"…\" />",
        });
        await Assert.That(run.WithId("NEXNET045")[0].Severity).IsEqualTo(DiagnosticSeverity.Error);
    }

    [Test]
    public async Task InvalidJavaPackage_Is045()
    {
        var options = BothConfigured();
        options["build_property.NexNetTarget_java_Package"] = "com.default.app";
        var run = Run(options, Source("ValueTask<int> Add(int a, int b);"));
        await Assert.That(run.Messages("NEXNET045")).IsEquivalentTo(new[]
        {
            "Invalid NexNet target configuration: 'com.default.app' is not a valid Java package name",
        });
    }

    [Test]
    public async Task UnknownTargetsValue_Is045()
    {
        var run = RunBoth(Source("ValueTask<int> Add(int a, int b);", "NexusTargets.Java | (NexusTargets)4"));
        await Assert.That(run.Messages("NEXNET045")).IsEquivalentTo(new[]
        {
            "Invalid NexNet target configuration: 'Demo.ServerNexus' requests the unknown NexusTargets value 4",
        });
    }

    // ------------------------------------------------------------------ NEXNET046

    [Test]
    public async Task DateTimeDictionaryKey_Is046InJavaScriptOnly()
    {
        var run = RunBoth(Source("ValueTask Count(Dictionary<DateTime, int> perDay, HashSet<string> names);"));

        await Assert.That(run.Messages("NEXNET046")).IsEquivalentTo(new[]
        {
            "parameter 'perDay' of 'Demo.IServerNexus.Count' uses a set element or dictionary key that compares by reference in JavaScript (key type 'DateTime' maps to 'NexusDateTime'); a lookup with an equal value does not find the entry",
        });
        await Assert.That(run.WithId("NEXNET046")[0].Severity).IsEqualTo(DiagnosticSeverity.Warning);
    }

    [Test]
    public async Task NestedObjectSetElement_Is046InJavaScriptOnly()
    {
        var run = RunBoth(Source(
            "ValueTask<List<HashSet<Point>>> Shapes();",
            types: "[NexusObject] public class Point { [NexusKey(0)] public int X; }"));

        var messages = run.Messages("NEXNET046");
        await Assert.That(messages.Length).IsEqualTo(1);
        await Assert.That(messages[0]).Contains("in JavaScript (key type 'Demo.Point' maps to 'Point");
    }

    // ------------------------------------------------------------------ NEXNET047

    private const string TwoChats = Usings + """
        [assembly: NexusClientExport<A.IServerNexus, A.IClientNexus>(Targets = NexusTargets.Java | NexusTargets.JavaScript, Name = "Chat")]
        [assembly: NexusClientExport<B.IServerNexus, B.IClientNexus>(Targets = NexusTargets.Java | NexusTargets.JavaScript, Name = "Chat")]

        namespace A
        {
            public interface IClientNexus { ValueTask Notify(string message); }
            public interface IServerNexus { ValueTask Send(string message); }
        }

        namespace B
        {
            public interface IClientNexus { ValueTask Notify(string message); }
            public interface IServerNexus { ValueTask Send(string message, int room); }
        }
        """;

    [Test]
    public async Task ExportsWithTheSameNameInDifferentNamespaces_Are047()
    {
        var run = RunBoth(TwoChats);

        await Assert.That(run.Messages("NEXNET047")).IsEquivalentTo(new[]
        {
            "Output file collision (Java): 'com/acme/ChatHandshake.java' would be written by export 'A.Chat' and export 'B.Chat'",
            "Output file collision (Java): 'com/acme/ChatServerProxy.java' would be written by export 'A.Chat' and export 'B.Chat'",
            "Output file collision (Java): 'com/acme/ChatClientNexusBase.java' would be written by export 'A.Chat' and export 'B.Chat'",
            "Output file collision (Java): 'com/acme/ChatClient.java' would be written by export 'A.Chat' and export 'B.Chat'",
            "Output file collision (JavaScript): 'Chat.js' would be written by export 'A.Chat' and export 'B.Chat'",
            "Output file collision (JavaScript): 'Chat.d.ts' would be written by export 'A.Chat' and export 'B.Chat'",
        });
        await Assert.That(run.WithId("NEXNET047").All(d => d.Severity == DiagnosticSeverity.Error)).IsTrue();
    }

    [Test]
    public async Task TypesWithTheSameNameOrAnExportName_Are047()
    {
        var run = Run(new Dictionary<string, string> { ["build_property.NexNetTargets"] = "javascript" }, Usings + """
            namespace Demo
            {
                public partial interface IClientNexus { ValueTask Notify(string message); }
                public partial interface IServerNexus { ValueTask Put(A.Item a, B.ITEM b, ServerNexus c); }

                [Nexus<IClientNexus, IServerNexus>(NexusType = NexusType.Client)]
                public partial class ClientNexus { }

                [Nexus<IServerNexus, IClientNexus>(NexusType = NexusType.Server, Targets = NexusTargets.JavaScript)]
                public partial class ServerNexus { }

                [NexusObject] public class ServerNexusData { [NexusKey(0)] public int X; }
            }

            namespace Demo.A { [NexusObject] public class Item { [NexusKey(0)] public int X; } }
            namespace Demo.B { [NexusObject] public class ITEM { [NexusKey(0)] public int X; } }
            """.Replace("ServerNexus c", "Demo.Other.ServerNexus c") + """

            namespace Demo.Other { [NexusObject] public class ServerNexus { [NexusKey(0)] public int X; } }
            """);

        await Assert.That(run.Messages("NEXNET047")).IsEquivalentTo(new[]
        {
            "Output file collision (JavaScript): 'Item.js' would be written by type 'Demo.A.Item' and type 'Demo.B.ITEM'",
            "Output file collision (JavaScript): 'Item.d.ts' would be written by type 'Demo.A.Item' and type 'Demo.B.ITEM'",
            "Output file collision (JavaScript): 'ServerNexus.js' would be written by export 'Demo.ServerNexus' and type 'Demo.Other.ServerNexus'",
            "Output file collision (JavaScript): 'ServerNexus.d.ts' would be written by export 'Demo.ServerNexus' and type 'Demo.Other.ServerNexus'",
        });
    }

    // ------------------------------------------------------------------ determinism

    [Test]
    public async Task Validation_IsDeterministic()
    {
        var first = RunBoth(TwoChats);
        var second = RunBoth(TwoChats);
        var config = TargetRegistry.ParseConfiguration(new EmptyOptions());

        await Assert.That(first.Diagnostics.Select(d => d.ToString()).ToArray())
            .IsEquivalentTo(second.Diagnostics.Select(d => d.ToString()).ToArray(), TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(TargetValidator.Validate(null, config)).IsEmpty();
    }

    private sealed class EmptyOptions : Microsoft.CodeAnalysis.Diagnostics.AnalyzerConfigOptions
    {
        public override bool TryGetValue(string key, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? value)
        {
            value = null;
            return false;
        }
    }
}
