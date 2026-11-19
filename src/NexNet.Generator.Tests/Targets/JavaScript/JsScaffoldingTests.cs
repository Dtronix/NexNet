using System.Security.Cryptography;
using System.Text;
using NexNet.Generator.Bundle;
using NexNet.Generator.Extraction;
using NexNet.Generator.Ir;
using NexNet.Generator.Serialization;
using NexNet.Generator.Targets;
using NexNet.Generator.Targets.JavaScript;

namespace NexNet.Generator.Tests.Targets.JavaScript;

/// <summary>The JavaScript test scaffolding emitter (J11): text snapshots and structure; no Node needed.</summary>
public class JsScaffoldingTests
{
    private const string Dir = JsScaffoldingEmitter.Directory;

    private static JsRun? _interop;
    private static JsScaffoldRun? _chat;
    private static JsScaffoldRun? _interopScaffolding;

    /// <summary>The real interop nexus of NexNet.TestNexus (H02): the exports the Node and browser hosts (J12) drive.</summary>
    internal static JsRun Interop => _interop ??= JsTestRunner.Run(JsScaffoldFixtures.Interop());

    internal static JsScaffoldRun InteropScaffolding => _interopScaffolding ??= JsScaffoldRun.Of(Interop);

    internal static JsScaffoldRun Chat => _chat ??= JsScaffoldRun.Of(JsProxyEmitterTests.Chat);

    // ------------------------------------------------------------------ snapshots

    [Test]
    public async Task InteropRunsClean()
    {
        await Assert.That(Interop.Ir).IsNotNull();
        // NEXNET044 (no Java output directory) is expected: only the JavaScript target is configured here.
        await Assert.That(Interop.Diagnostics.Where(d => d.Id != "NEXNET044").Select(d => d.ToString())).IsEmpty();
        await Assert.That(Interop.Ir!.Exports.Count(e => (e.Targets & (int)NexusTargets.JavaScript) != 0)).IsGreaterThanOrEqualTo(3);
    }

    [Test]
    public Task ChatSnapshot()
        => JsSnapshots.Verify("chat-scaffolding", System.Text.RegularExpressions.Regex.Replace(
            Chat.Concatenated(withRuntime: false), "SCHEMA_HASH = '[0-9a-f]{64}'", "SCHEMA_HASH = '<sha256 of the IR, see SchemaHashIsTheSha256OfTheIrFile>'"));

    [Test]
    public Task RuntimeSnapshot() => JsSnapshots.Verify("scaffolding-runtime", Chat.ConcatenatedRuntime());

    // ------------------------------------------------------------------ layout

    [Test]
    public async Task EveryFileIsInTheScaffoldingDirectory()
    {
        var expected = new[] { "ChatScaffolding", JsScaffoldingEmitter.CodecsModule, JsScaffoldingEmitter.IndexModule }
            .SelectMany(n => new[] { n + ".js", n + ".d.ts" })
            .Concat(JsScaffoldingRuntime.Names)
            .Select(n => Dir + n)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToArray();
        await Assert.That(Chat.Files.Keys.OrderBy(p => p, StringComparer.Ordinal)).IsEquivalentTo(expected);
        foreach (var (_, content) in Chat.Files)
        {
            await Assert.That(content).StartsWith(JsSyntax.Header + "\n");
            await Assert.That(content).EndsWith("\n");
            await Assert.That(content).DoesNotContain("\r");
            await Assert.That(content).DoesNotContain("$RUNTIME$");
        }
    }

    [Test]
    public async Task OneModulePerExport()
    {
        var exports = Interop.Ir!.Exports.Where(e => (e.Targets & (int)NexusTargets.JavaScript) != 0).ToArray();
        foreach (var export in exports)
        {
            var names = JsScaffoldingNames.Of(export);
            var module = InteropScaffolding.File(names.Scaffolding + ".js");
            await Assert.That(InteropScaffolding.Files.Keys).Contains(Dir + names.Scaffolding + ".d.ts");
            await Assert.That(module).Contains("export class " + names.ScriptableNexus + " extends $m." + JsExportNames.Of(export).ClientNexusBase + " {");
            await Assert.That(module).Contains("clientNexus: " + JsSyntax.String(export.ClientApi.ClrName) + ",");
            await Assert.That(module).Contains("serverNexus: " + JsSyntax.String(export.ServerApi.ClrName) + ",");
            await Assert.That(module).Contains("export const " + names.Scaffolding + " = new " + names.Scaffolding + "Export();");
            await Assert.That(InteropScaffolding.File("index.js")).Contains("import { " + names.Scaffolding + " } from './" + names.Scaffolding + ".js';");
        }

        await Assert.That(exports.Select(e => e.ClientApi.ClrName)).Contains("NexNet.TestNexus.Interop.IInteropClientNexus");
        await Assert.That(exports.Select(e => e.ServerApi.ClrName)).Contains("NexNet.TestNexus.Interop.IInteropServerNexus");
    }

    [Test]
    public async Task ClientModeEmitsNoScaffolding()
    {
        await Assert.That(JsProxyEmitterTests.Chat.Files.Keys.Any(k => k.StartsWith(Dir, StringComparison.Ordinal))).IsFalse();
        await Assert.That(Interop.Files.Keys.Any(k => k.StartsWith(Dir, StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    public async Task NoScaffoldingWithoutAJavaScriptExport()
    {
        var ir = JsProxyEmitterTests.Chat.Ir!;
        ir = ir with { Exports = new(ir.Exports.Select(e => e with { Targets = (int)NexusTargets.Java }).ToArray()) };
        var files = JavaScriptTarget.Instance.Emit(ir, JsTestRunner.TargetOptionsFor(), EmitMode.TestScaffolding);
        await Assert.That(files).IsEmpty();
    }

    [Test]
    public async Task OutputIsDeterministic()
    {
        var again = JsScaffoldRun.Of(JsTestRunner.Run(JsProxyFixtures.Chat));
        await Assert.That(again.Concatenated(withRuntime: true)).IsEqualTo(Chat.Concatenated(withRuntime: true));
    }

    [Test]
    public async Task ModuleNameOptionChangesTheRuntimeImport()
    {
        var run = JsScaffoldRun.Of(JsTestRunner.RunWith("nexnet-local", JsProxyFixtures.Chat), "nexnet-local");
        foreach (var name in new[] { "ChatScaffolding.js", "ChatScaffolding.d.ts", "test-codecs.js", "typed-json.js", "scaffolding-runtime.js" })
            await Assert.That(run.File(name)).Contains("from 'nexnet-local';");
        await Assert.That(run.File("scaffolding-runtime.d.ts")).Contains("from 'nexnet-local';");
        await Assert.That(run.Concatenated(withRuntime: true)).DoesNotContain("@dtronix/nexnet");
    }

    // ------------------------------------------------------------------ option and bundle

    [Test]
    public async Task TheOptionIsParsed()
    {
        var options = JavaScriptTarget.Instance.ParseOptions(new TestOptions(new Dictionary<string, string>
        {
            ["build_property.NexNetTargets"] = "javascript",
            ["build_property.NexNetTarget_javascript_EmitTestScaffolding"] = "true",
        }));
        await Assert.That(options.Get(TargetBundleBuilder.TestScaffoldingOption)).IsEqualTo("true");
        await Assert.That(JavaScriptTarget.TestScaffoldingOption).IsEqualTo(TargetBundleBuilder.TestScaffoldingOption);
    }

    [Test]
    public async Task TheBundleCarriesTheScaffoldingOnlyWhenAskedFor()
    {
        var ir = JsProxyEmitterTests.Chat.Ir!;
        IReadOnlyList<GeneratedFile> Bundle(bool scaffolding)
        {
            // Without the emitted runtime (D31), which test scaffolding turns off by default.
            var values = new List<TargetOption> { new("EmitRuntime", "false") };
            if (scaffolding)
                values.Add(new TargetOption(JavaScriptTarget.TestScaffoldingOption, "true"));
            var configuration = new TargetConfiguration(
                new EquatableArray<TargetOptions>(new[] { new TargetOptions("javascript", true, new EquatableArray<TargetOption>(values.ToArray())) }),
                EquatableArray<string>.Empty);
            return TargetBundleBuilder.Files(new TargetCheck(ir, configuration, EquatableArray<TargetDiagnostic>.Empty))!;
        }

        var without = Bundle(false).Select(f => f.Path).ToArray();
        var with = Bundle(true).Select(f => f.Path).ToArray();
        await Assert.That(without.Any(p => p.Contains("/" + Dir, StringComparison.Ordinal))).IsFalse();
        await Assert.That(with).Contains("javascript/" + Dir + "ChatScaffolding.js");
        await Assert.That(with.Except(without).All(p => p.StartsWith("javascript/" + Dir, StringComparison.Ordinal))).IsTrue();
        await Assert.That(with.Length - without.Length).IsEqualTo(Chat.Files.Count);
    }

    // ------------------------------------------------------------------ content

    [Test]
    public async Task SchemaHashIsTheSha256OfTheIrFile()
    {
        var bytes = new UTF8Encoding(false).GetBytes(IrJson.Write(Interop.Ir!));
        var expected = Convert.ToHexStringLower(SHA256.HashData(bytes));
        await Assert.That(InteropScaffolding.File("index.js")).Contains("export const SCHEMA_HASH = '" + expected + "';");
        await Assert.That(JsScaffoldingEmitter.SchemaHash(Interop.Ir!)).IsEqualTo(expected);
        // The Java host (K11) reports the same hash for the same IR.
        await Assert.That(NexNet.Generator.Targets.Java.ScaffoldingEmitter.SchemaHash(Interop.Ir!)).IsEqualTo(expected);
    }

    [Test]
    public async Task ScriptableNexusOverridesEveryClientMethod()
    {
        var module = Chat.File("ChatScaffolding.js");
        await Assert.That(module).Contains("export class ChatScriptableNexus extends $m.ChatClientNexusBase {");
        await Assert.That(module).Contains("  async square(value, cancellationToken) {\n    return $tc.C$");
        await Assert.That(module).Contains(".fromJson(await $sr.runScript(this.#host, 'Square', [$tc.C$");
        await Assert.That(module).Contains(".toJson(value)], cancellationToken, null, null));");
        await Assert.That(module).Contains("  message(text, money) {\n    $sr.runDetached(this.#host, 'Message', [$tc.C$");
        await Assert.That(module).Contains("  async receive(pipe) {\n    await $sr.runScript(this.#host, 'Receive', [], null, pipe, null);");
        await Assert.That(module).Contains("await $sr.runScript(this.#host, 'Feed', [$tc.C$");
        await Assert.That(module).Contains("  async onConnected_(arguments_, context, methodId) {");
        await Assert.That(module).Contains("$sr.runDetached(this.#host, $sr.ON_CONNECTED, [isReconnected]);");
        await Assert.That(module).Contains("$sr.runDetached(this.#host, $sr.ON_DISCONNECTED, []);");
        foreach (var method in JsProxyEmitterTests.Chat.Ir!.Exports.Single().ClientApi.Methods)
            await Assert.That(module).Contains("'" + method.Name + "'");
    }

    [Test]
    public async Task InvokeByNameCoversEveryServerMethod()
    {
        var module = Chat.File("ChatScaffolding.js");
        foreach (var method in JsProxyEmitterTests.Chat.Ir!.Exports.Single().ServerApi.Methods)
            await Assert.That(module).Contains("      case " + JsSyntax.String(method.Name) + ": {\n");

        await Assert.That(module).Contains("proxy.notify($tc.C$");
        await Assert.That(module).Contains("return null;");
        await Assert.That(module).Contains("return proxy.ping().then($se.nothing);");
        await Assert.That(module).Contains(".fromJson(args[1]), { signal }).then((r) => $tc.C$");
        await Assert.That(module).Contains("return proxy.upload($se.pipe(pipe), $tc.C$");
        await Assert.That(module).Contains("return proxy.stream($se.channel(pipe)).then($se.nothing);");
        await Assert.That(module).Contains("return proxy.wait({ signal }).then((r) => $tc.C$");
        await Assert.That(module).Contains("      case 'List':\n        return proxy.list;");
        await Assert.That(module).Contains("new $sr.Collection('Boxes', 13, $rt.NexusCollectionMode.Relay, $tc.C$");
        await Assert.That(module).Contains("new $sr.Method('Stream', 6, 'Task', 'Channel', false, [], null, $tc.C$");
        await Assert.That(module).Contains("new $sr.Method('Notify', 0, 'Void', 'None', false, [$tc.C$");
    }

    [Test]
    public async Task UserFormattersAreImportedFromTheOutputDirectory()
    {
        await Assert.That(Chat.File("test-codecs.js")).Contains("import { MoneyFormatter as $user0 } from '../ext/money-formatter.js';");
        await Assert.That(InteropScaffolding.File("test-codecs.js")).Contains("import { InteropPointFormatter as $user0 } from '../interop-point-formatter.js';");
        await Assert.That(JsScaffoldingEmitter.FromScaffolding("./a.js")).IsEqualTo("../a.js");
        await Assert.That(JsScaffoldingEmitter.FromScaffolding("../a.js")).IsEqualTo("../../a.js");
        await Assert.That(JsScaffoldingEmitter.FromScaffolding("pkg/a")).IsEqualTo("pkg/a");
    }

    [Test]
    public async Task ChannelTypesAreFoundByTheRunnersExpression()
    {
        var codecs = InteropScaffolding.File("test-codecs.js");
        await Assert.That(codecs).Contains("['long', C$");
        await Assert.That(codecs).Contains("['NexNet.TestNexus.Interop.InteropItem', C$");
        await Assert.That(codecs).Contains("['NexNet.TestNexus.Interop.InteropBox" + (char)96 + "1<string>', C$");
    }

    [Test]
    public async Task ExpressionsAreTheJavaTargets()
    {
        // The runner names channel types once for every language host.
        var types = new List<TypeRef>();
        void Visit(TypeRef? type)
        {
            if (type == null)
                return;
            types.Add(type);
            foreach (var arg in type.Args)
                Visit(arg);
        }

        foreach (var export in Interop.Ir!.Exports)
        {
            foreach (var api in new[] { export.ServerApi, export.ClientApi })
            {
                foreach (var method in api.Methods)
                {
                    Visit(method.ReturnType);
                    Visit(method.PipeType);
                    foreach (var param in method.Params)
                        Visit(param.Type);
                }
            }
        }

        await Assert.That(types.Count).IsGreaterThan(50);
        foreach (var type in types)
            await Assert.That(JsScaffoldingEmitter.Expression(type)).IsEqualTo(NexNet.Generator.Targets.Java.ScaffoldingEmitter.Expression(type));
    }

    private sealed class TestOptions : Microsoft.CodeAnalysis.Diagnostics.AnalyzerConfigOptions
    {
        private readonly Dictionary<string, string> _values;

        public TestOptions(Dictionary<string, string> values) => _values = new Dictionary<string, string>(values, KeyComparer);

        public override bool TryGetValue(string key, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? value) => _values.TryGetValue(key, out value);
    }
}

/// <summary>The scaffolding files of one generator run.</summary>
internal sealed record JsScaffoldRun(IReadOnlyDictionary<string, string> Files)
{
    public static JsScaffoldRun Of(JsRun run, string? moduleName = null)
        => new(JavaScriptTarget.Instance.Emit(run.Ir!, JsTestRunner.TargetOptionsFor(moduleName), EmitMode.TestScaffolding)
            .ToDictionary(f => f.Path, f => f.Content, StringComparer.Ordinal));

    /// <summary>A file by name in the scaffolding directory.</summary>
    public string File(string name)
    {
        var path = JsScaffoldingEmitter.Directory + name;
        return Files.TryGetValue(path, out var content)
            ? content
            : throw new InvalidOperationException("No file " + path + ". Emitted: " + string.Join(", ", Files.Keys));
    }

    private static bool IsRuntime(string path) => JsScaffoldingRuntime.Names.Any(n => path == JsScaffoldingEmitter.Directory + n);

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
internal static class JsScaffoldFixtures
{
    /// <summary>Every source of <c>src/NexNet.TestNexus/Interop</c>, with the project's implicit usings.</summary>
    public static string[] Interop()
    {
        var folder = Path.Combine(JsTestRunner.RepoRoot(), "src", "NexNet.TestNexus", "Interop");
        var files = Directory.GetFiles(folder, "*.cs").OrderBy(f => f, StringComparer.Ordinal).Select(System.IO.File.ReadAllText).ToArray();
        files[0] = "global using System;\nglobal using System.Collections.Generic;\nglobal using System.Linq;\nglobal using System.Threading;\nglobal using System.Threading.Tasks;\n" + files[0];
        return files;
    }
}
