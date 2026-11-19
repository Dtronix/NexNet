using System.Security.Cryptography;
using System.Text;
using NexNet.Generator.Bundle;
using NexNet.Generator.Extraction;
using NexNet.Generator.Ir;
using NexNet.Generator.Serialization;
using NexNet.Generator.Targets;
using NexNet.Generator.Targets.Java;

namespace NexNet.Generator.Tests.Targets.Java;

/// <summary>The Java test scaffolding emitter (K11): text snapshots and structure; no JDK needed.</summary>
public class JavaScaffoldingTests
{
    private const string Scaffolding = "scaffolding/";

    private static JavaRun? _interop;
    private static ScaffoldRun? _chat;
    private static ScaffoldRun? _interopScaffolding;

    /// <summary>The real interop nexus of NexNet.TestNexus (H02): the exports the JVM host (K12) drives.</summary>
    internal static JavaRun Interop => _interop ??= JavaTestRunner.Run(JavaFixtures.ZooPackage, JavaScaffoldFixtures.Interop());

    internal static ScaffoldRun InteropScaffolding => _interopScaffolding ??= ScaffoldRun.Of(Interop, JavaFixtures.ZooPackage);

    internal static ScaffoldRun Chat => _chat ??= ScaffoldRun.Of(JavaProxyEmitterTests.Chat, JavaFixtures.ChatPackage);

    // ------------------------------------------------------------------ snapshots

    [Test]
    public async Task InteropRunsClean()
    {
        await Assert.That(Interop.Ir).IsNotNull();
        // NEXNET044 (no JavaScript output directory) is expected: only the Java target is configured here.
        await Assert.That(Interop.Diagnostics.Where(d => d.Id != "NEXNET044").Select(d => d.ToString())).IsEmpty();
        await Assert.That(Interop.Ir!.Exports.Count(e => (e.Targets & (int)NexusTargets.Java) != 0)).IsGreaterThanOrEqualTo(3);
    }

    [Test]
    public Task ChatSnapshot()
        => JavaSnapshots.Verify("chat-scaffolding", System.Text.RegularExpressions.Regex.Replace(
            Chat.Concatenated(withRuntime: false), "SCHEMA_HASH = \"[0-9a-f]{64}\"", "SCHEMA_HASH = \"<sha256 of the IR, see SchemaHashIsTheSha256OfTheIrFile>\""));

    [Test]
    public Task RuntimeSnapshot() => JavaSnapshots.Verify("scaffolding-runtime", Chat.ConcatenatedRuntime());

    // ------------------------------------------------------------------ layout

    [Test]
    public async Task EveryFileIsInTheScaffoldingSubPackage()
    {
        var expected = new[] { "ChatScaffolding", "ChatScriptableNexus", "Scaffolding", "TestCodecs" }
            .Concat(ScaffoldingRuntime.Names)
            .Select(n => "com/acme/chat/scaffolding/" + n + ".java")
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToArray();
        await Assert.That(Chat.Files.Keys.OrderBy(p => p, StringComparer.Ordinal)).IsEquivalentTo(expected);
        foreach (var (_, content) in Chat.Files)
        {
            await Assert.That(content).StartsWith(JavaSyntax.Header + "\npackage com.acme.chat.scaffolding;\n");
            await Assert.That(content).EndsWith("}\n");
            await Assert.That(content).DoesNotContain("\r");
            await Assert.That(content).DoesNotContain(ScaffoldingRuntime.PackagePlaceholder);
        }
    }

    [Test]
    public async Task OneScriptableNexusAndScaffoldingPerExport()
    {
        var exports = Interop.Ir!.Exports.Where(e => (e.Targets & (int)NexusTargets.Java) != 0).ToArray();
        foreach (var export in exports)
        {
            var names = ScaffoldingNames.Of(export);
            await Assert.That(InteropScaffolding.Files.Keys).Contains("com/dtronix/nexnet/testnexus/scaffolding/" + names.ScriptableNexus + ".java");
            await Assert.That(InteropScaffolding.Files.Keys).Contains("com/dtronix/nexnet/testnexus/scaffolding/" + names.Scaffolding + ".java");
            await Assert.That(InteropScaffolding.File(names.Scaffolding)).Contains("return " + JavaSyntax.StringLiteral(export.ClientApi.ClrName) + ";");
            await Assert.That(InteropScaffolding.File(names.Scaffolding)).Contains("return " + JavaSyntax.StringLiteral(export.ServerApi.ClrName) + ";");
            await Assert.That(InteropScaffolding.File("Scaffolding")).Contains(names.Scaffolding + ".INSTANCE");
        }

        await Assert.That(exports.Select(e => e.ClientApi.ClrName)).Contains("NexNet.TestNexus.Interop.IInteropClientNexus");
        await Assert.That(exports.Select(e => e.ServerApi.ClrName)).Contains("NexNet.TestNexus.Interop.IInteropServerNexus");
    }

    [Test]
    public async Task ClientModeEmitsNoScaffolding()
    {
        await Assert.That(JavaProxyEmitterTests.Chat.Files.Keys.Any(k => k.Contains("/" + Scaffolding, StringComparison.Ordinal))).IsFalse();
        await Assert.That(JavaEmitterTests.Features.Files.Keys.Any(k => k.Contains("/" + Scaffolding, StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    public async Task NoScaffoldingWithoutAJavaExport()
    {
        var ir = JavaProxyEmitterTests.Chat.Ir!;
        ir = ir with { Exports = new(ir.Exports.Select(e => e with { Targets = (int)NexusTargets.JavaScript }).ToArray()) };
        var files = JavaTarget.Instance.Emit(ir, JavaTestRunner.TargetOptionsFor(JavaFixtures.ChatPackage), EmitMode.TestScaffolding);
        await Assert.That(files).IsEmpty();
    }

    [Test]
    public async Task OutputIsDeterministic()
    {
        var again = ScaffoldRun.Of(JavaTestRunner.Run(JavaFixtures.ChatPackage, JavaFixtures.Chat), JavaFixtures.ChatPackage);
        await Assert.That(again.Concatenated(withRuntime: true)).IsEqualTo(Chat.Concatenated(withRuntime: true));
    }

    // ------------------------------------------------------------------ option and bundle

    [Test]
    public async Task TheOptionIsParsed()
    {
        var options = JavaTarget.Instance.ParseOptions(new TestOptions(new Dictionary<string, string>
        {
            ["build_property.NexNetTargets"] = "java",
            ["build_property.NexNetTarget_java_Package"] = "com.acme",
            ["build_property.NexNetTarget_java_EmitTestScaffolding"] = "true",
        }));
        await Assert.That(options.Get(TargetBundleBuilder.TestScaffoldingOption)).IsEqualTo("true");
        await Assert.That(JavaTarget.TestScaffoldingOption).IsEqualTo(TargetBundleBuilder.TestScaffoldingOption);
    }

    [Test]
    public async Task TheBundleCarriesTheScaffoldingOnlyWhenAskedFor()
    {
        var ir = JavaProxyEmitterTests.Chat.Ir!;
        IReadOnlyList<GeneratedFile> Bundle(bool scaffolding)
        {
            // Without the emitted runtime (D31), which test scaffolding turns off by default.
            var values = new List<TargetOption> { new(JavaTarget.PackageOption, JavaFixtures.ChatPackage), new("EmitRuntime", "false") };
            if (scaffolding)
                values.Add(new TargetOption(JavaTarget.TestScaffoldingOption, "true"));
            var configuration = new TargetConfiguration(
                new EquatableArray<TargetOptions>(new[] { new TargetOptions("java", true, new EquatableArray<TargetOption>(values.ToArray())) }),
                EquatableArray<string>.Empty);
            return TargetBundleBuilder.Files(new TargetCheck(ir, configuration, EquatableArray<TargetDiagnostic>.Empty))!;
        }

        var without = Bundle(false).Select(f => f.Path).ToArray();
        var with = Bundle(true).Select(f => f.Path).ToArray();
        await Assert.That(without.Any(p => p.Contains("/" + Scaffolding, StringComparison.Ordinal))).IsFalse();
        await Assert.That(with).Contains("java/com/acme/chat/scaffolding/ChatScriptableNexus.java");
        await Assert.That(with.Except(without).All(p => p.StartsWith("java/com/acme/chat/scaffolding/", StringComparison.Ordinal))).IsTrue();
        await Assert.That(with.Length - without.Length).IsEqualTo(Chat.Files.Count);
    }

    // ------------------------------------------------------------------ content

    [Test]
    public async Task SchemaHashIsTheSha256OfTheIrFile()
    {
        var bytes = new UTF8Encoding(false).GetBytes(IrJson.Write(Interop.Ir!));
        var expected = Convert.ToHexStringLower(SHA256.HashData(bytes));
        await Assert.That(InteropScaffolding.File("Scaffolding")).Contains("public static final String SCHEMA_HASH = \"" + expected + "\";");
        await Assert.That(ScaffoldingEmitter.SchemaHash(Interop.Ir!)).IsEqualTo(expected);
    }

    [Test]
    public async Task ScriptableNexusOverridesEveryClientMethod()
    {
        var nexus = Chat.File("ChatScriptableNexus");
        await Assert.That(nexus).Contains("public final class ChatScriptableNexus extends com.acme.chat.ChatClientNexusBase {");
        await Assert.That(nexus).Contains("protected CompletableFuture<Integer> square(int value, CancellationToken cancellationToken) {\n"
                                          + "        return ScriptCall.run(host$, \"Square\", ScriptCall.args(TestCodecs.C$");
        await Assert.That(nexus).Contains(".toJson(value)), cancellationToken, null, null).thenApply(TestCodecs.C$");
        await Assert.That(nexus).Contains("protected void message(String text, Object money) {\n"
                                          + "        ScriptCall.run(host$, \"Message\", ScriptCall.args(");
        await Assert.That(nexus).Contains("protected CompletableFuture<Void> receive(NexusDuplexPipe pipe) {\n"
                                          + "        return ScriptCall.run(host$, \"Receive\", ScriptCall.args(), CancellationToken.NONE, pipe, null).thenApply(r$ -> null);");
        await Assert.That(nexus).Contains("ScriptCall.run(host$, \"Feed\", ScriptCall.args(TestCodecs.C$");
        await Assert.That(nexus).Contains("protected CompletableFuture<Void> dispatch_(int arguments, int context, int methodId) {");
        await Assert.That(nexus).Contains("ScriptCall.run(host$, ScriptCall.ON_CONNECTED, ScriptCall.args(isReconnected), CancellationToken.NONE, null, null);");
        await Assert.That(nexus).Contains("ScriptCall.run(host$, ScriptCall.ON_DISCONNECTED, ScriptCall.args(), CancellationToken.NONE, null, null);");
    }

    [Test]
    public async Task InvokeByNameCoversEveryServerMethod()
    {
        var export = Chat.File("ChatScaffolding");
        foreach (var method in JavaProxyEmitterTests.Chat.Ir!.Exports.Single().ServerApi.Methods)
            await Assert.That(export).Contains("case " + JavaSyntax.StringLiteral(method.Name) + ": {");

        await Assert.That(export).Contains("proxy.notify_(TestCodecs.C$");
        await Assert.That(export).Contains("return sent();");
        await Assert.That(export).Contains("return proxy.ping().thenApply(ScaffoldingExport::nothing);");
        await Assert.That(export).Contains(".fromJson(arguments.get(1)), cancellationToken).thenApply(TestCodecs.C$");
        await Assert.That(export).Contains("return proxy.upload(pipe(pipe), TestCodecs.C$");
        await Assert.That(export).Contains("return proxy.stream(channel(pipe)).thenApply(ScaffoldingExport::nothing);");
        await Assert.That(export).Contains("return proxy.wait_(cancellationToken).thenApply(TestCodecs.C$");
        await Assert.That(export).Contains("case \"List\":\n                return proxy.list_();");
        await Assert.That(export).Contains("new Collection(\"Boxes\", 12, NexusCollectionMode.RELAY, TestCodecs.C$");
        await Assert.That(export).Contains("new Method(\"Stream\", 6, ReturnKind.TASK, PipeKind.CHANNEL, false, Collections.<TypedCodec<?>>emptyList(), null, TestCodecs.C$");
    }

    [Test]
    public async Task ChannelTypesAreFoundByTheRunnersExpression()
    {
        var codecs = InteropScaffolding.File("TestCodecs");
        await Assert.That(codecs).Contains("case \"long\":");
        await Assert.That(codecs).Contains("case \"NexNet.TestNexus.Interop.InteropItem\":");
        await Assert.That(codecs).Contains("case \"NexNet.TestNexus.Interop.InteropBox`1<string>\":");
    }

    [Test]
    public async Task ExpressionsMatchTheGoldenTypeGrammar()
    {
        TypeRef Prim(PrimKind p) => new(TypeRefKind.Prim) { Prim = p };
        await Assert.That(ScaffoldingEmitter.Expression(Prim(PrimKind.Int32))).IsEqualTo("int");
        await Assert.That(ScaffoldingEmitter.Expression(Prim(PrimKind.DateTimeOffset))).IsEqualTo("DateTimeOffset");
        await Assert.That(ScaffoldingEmitter.Expression(new TypeRef(TypeRefKind.PrimArray) { Prim = PrimKind.Half, Clr = "List" })).IsEqualTo("half[]");
        await Assert.That(ScaffoldingEmitter.Expression(new TypeRef(TypeRefKind.Seq) { Clr = "Array", Args = new(new[] { Prim(PrimKind.String) }) })).IsEqualTo("string[]");
        await Assert.That(ScaffoldingEmitter.Expression(new TypeRef(TypeRefKind.Seq) { Clr = "IReadOnlyList", Args = new(new[] { Prim(PrimKind.String) }) })).IsEqualTo("IReadOnlyList<string>");
        await Assert.That(ScaffoldingEmitter.Expression(new TypeRef(TypeRefKind.Map) { Clr = "IDictionary", Args = new(new[] { Prim(PrimKind.Int32), Prim(PrimKind.String) }) })).IsEqualTo("Dictionary<int,string>");
        await Assert.That(ScaffoldingEmitter.Expression(new TypeRef(TypeRefKind.Tuple) { Clr = "ValueTuple", Args = new(new[] { Prim(PrimKind.Int32), new TypeRef(TypeRefKind.Nullable) { Args = new(new[] { Prim(PrimKind.Guid) }) } }) })).IsEqualTo("(int,Guid?)");
        await Assert.That(ScaffoldingEmitter.Expression(new TypeRef(TypeRefKind.Decl) { DeclId = "Ns.Box`1", Args = new(new[] { Prim(PrimKind.Byte) }) })).IsEqualTo("Ns.Box`1<byte>");
    }

    private sealed class TestOptions : Microsoft.CodeAnalysis.Diagnostics.AnalyzerConfigOptions
    {
        private readonly Dictionary<string, string> _values;

        public TestOptions(Dictionary<string, string> values) => _values = new Dictionary<string, string>(values, KeyComparer);

        public override bool TryGetValue(string key, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? value) => _values.TryGetValue(key, out value);
    }
}

/// <summary>The scaffolding files of one generator run.</summary>
internal sealed record ScaffoldRun(string Package, IReadOnlyDictionary<string, string> Files)
{
    public static ScaffoldRun Of(JavaRun run, string package)
        => new(package, JavaTarget.Instance.Emit(run.Ir!, JavaTestRunner.TargetOptionsFor(package), EmitMode.TestScaffolding)
            .ToDictionary(f => f.Path, f => f.Content, StringComparer.Ordinal));

    /// <summary>A file by class name.</summary>
    public string File(string className)
    {
        var path = Package.Replace('.', '/') + "/scaffolding/" + className + ".java";
        return Files.TryGetValue(path, out var content)
            ? content
            : throw new InvalidOperationException("No file " + path + ". Emitted: " + string.Join(", ", Files.Keys));
    }

    private static bool IsRuntime(string path) => ScaffoldingRuntime.Names.Any(n => path.EndsWith("/" + n + ".java", StringComparison.Ordinal));

    /// <summary>The files in one text, path-ordered; the fixed runtime files only when asked for.</summary>
    public string Concatenated(bool withRuntime)
        => string.Concat(Files.Where(f => withRuntime || !IsRuntime(f.Key)).OrderBy(f => f.Key, StringComparer.Ordinal)
            .Select(f => "// ==== " + f.Key + "\n" + f.Value));

    /// <summary>The fixed runtime files.</summary>
    public string ConcatenatedRuntime()
        => string.Concat(Files.Where(f => IsRuntime(f.Key)).OrderBy(f => f.Key, StringComparer.Ordinal)
            .Select(f => "// ==== " + f.Key + "\n" + f.Value));
}

/// <summary>Sources for the scaffolding tests.</summary>
internal static class JavaScaffoldFixtures
{
    /// <summary>Every source of <c>src/NexNet.TestNexus/Interop</c>, with the project's implicit usings.</summary>
    public static string[] Interop()
    {
        var folder = Path.Combine(JavaTestRunner.RepoRoot(), "src", "NexNet.TestNexus", "Interop");
        var files = Directory.GetFiles(folder, "*.cs").OrderBy(f => f, StringComparer.Ordinal).Select(System.IO.File.ReadAllText).ToArray();
        files[0] = "global using System;\nglobal using System.Collections.Generic;\nglobal using System.Linq;\nglobal using System.Threading;\nglobal using System.Threading.Tasks;\n" + files[0];
        return files;
    }
}
