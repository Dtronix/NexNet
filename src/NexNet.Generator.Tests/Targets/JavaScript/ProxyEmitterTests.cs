using NexNet.Generator.Ir;
using NexNet.Generator.Targets;
using NexNet.Generator.Targets.JavaScript;

namespace NexNet.Generator.Tests.Targets.JavaScript;

/// <summary>The JavaScript proxy and client nexus emitters (J04): text snapshots and structure; no Node needed.</summary>
public class JsProxyEmitterTests
{
    private static JsRun? _chat;

    internal static JsRun Chat => _chat ??= JsTestRunner.Run(JsProxyFixtures.Chat);

    private static string Module => Chat.File("ChatServerNexus.js");

    private static string Declarations => Chat.File("ChatServerNexus.d.ts");

    /// <summary>Lines at an indentation, each ending in '\n'.</summary>
    private static string Lines(int indent, params string[] lines)
        => string.Concat(lines.Select(l => new string(' ', indent) + l + "\n"));

    [Test]
    public async Task ChatRunsClean()
    {
        await Assert.That(Chat.Ir).IsNotNull();
        await Assert.That(Chat.Diagnostics.Select(d => d.ToString())).IsEmpty();
        await Assert.That(Chat.CompileErrors.Select(d => d.ToString())).IsEmpty();
    }

    [Test]
    public Task ChatSnapshot()
        => JsSnapshots.Verify("chat", "// ==== ChatServerNexus.d.ts\n" + Declarations + "// ==== ChatServerNexus.js\n" + Module);

    [Test]
    public async Task ExportFilesAreTheEmittedFiles()
    {
        var options = JsTestRunner.TargetOptionsFor();
        var export = Chat.Ir!.Exports.Single();
        var expected = new[] { "ChatServerNexus.js", "ChatServerNexus.d.ts" };
        await Assert.That(JavaScriptTarget.Instance.ExportFiles(export, options)).IsEquivalentTo(expected);
        var typeFiles = Chat.Ir.Types.SelectMany(t => JavaScriptTarget.Instance.TypeFiles(t, options));
        var modules = new[] { "formatters.js", "formatters.d.ts", "nexnet-support.js", "nexnet-support.d.ts", "user-formatters.d.ts" };
        await Assert.That(Chat.Files.Keys.OrderBy(p => p, StringComparer.Ordinal))
            .IsEquivalentTo(expected.Concat(typeFiles).Concat(modules).OrderBy(p => p, StringComparer.Ordinal));
        await Assert.That(Module).StartsWith(JsSyntax.Header + "\n");
        await Assert.That(Declarations).StartsWith(JsSyntax.Header + "\n");
    }

    [Test]
    [Arguments("ChatServerNexus", "Chat")]
    [Arguments("ChatNexus", "Chat")]
    [Arguments("Chat", "Chat")]
    [Arguments("ServerNexus", "Server")]
    [Arguments("Nexus", "Nexus")]
    [Arguments("InteropVersionedServerNexusV1", "InteropVersionedServerNexusV1")]
    public async Task NamesComeFromTheExportName(string export, string stem)
    {
        // The same stems as the Java target (K04), so the clients line up.
        await Assert.That(JsExportNames.Stem(export)).IsEqualTo(stem);
        await Assert.That(JsExportNames.Stem(export)).IsEqualTo(NexNet.Generator.Targets.Java.JavaExportNames.Stem(export));
    }

    [Test]
    public async Task OutputIsDeterministic()
    {
        var again = JsTestRunner.Run(JsProxyFixtures.Chat);
        await Assert.That(again.Concatenated()).IsEqualTo(Chat.Concatenated());
        await Assert.That(Chat.Concatenated()).DoesNotContain("\r");
    }

    [Test]
    public async Task ModuleImportsOnlyDollarNames()
    {
        await Assert.That(Module).Contains("import * as $rt from '@dtronix/nexnet';\nimport * as $fmt from './formatters.js';\n"
                                           + "import { MoneyFormatter as $user0 } from './ext/money-formatter.js';\n");
        await Assert.That(Declarations).Contains("import * as $rt from '@dtronix/nexnet';\nimport type { Box } from './Box.js';\n");
        await Assert.That(Declarations).Contains("import type { Item } from './Item.js';\n");
    }

    [Test]
    public async Task ModuleNameOptionChangesTheRuntimeImport()
    {
        var run = JsTestRunner.RunWith("nexnet-local", JsProxyFixtures.Chat);
        await Assert.That(run.File("ChatServerNexus.js")).Contains("import * as $rt from 'nexnet-local';");
        await Assert.That(run.File("ChatServerNexus.d.ts")).Contains("import * as $rt from 'nexnet-local';");
        await Assert.That(run.File("ChatServerNexus.js")).Contains("@param {import('nexnet-local').ClientConfig} config");
    }

    // ------------------------------------------------------------------ handshake

    [Test]
    public async Task HandshakeHoldsTheIrConstants()
    {
        var export = Chat.Ir!.Exports.Single();
        await Assert.That(Module).Contains("export const ChatHandshake = Object.freeze({\n"
                                           + "  version: null,\n"
                                           + "  serverNexusHash: " + JsSyntax.Int(export.Handshake.ServerNexusHash) + ",\n"
                                           + "  clientNexusHash: " + JsSyntax.Int(export.Handshake.ClientNexusHash) + ",\n"
                                           + "  acceptedIds: new Set([" + string.Join(", ", export.ClientAcceptedIds.Select(a => a.Id)) + "]),\n"
                                           + "});");
        await Assert.That(Declarations).Contains("export declare const ChatHandshake: $rt.HandshakeDescriptor;");
    }

    [Test]
    public async Task VersionedHandshakeSendsTheVersion()
    {
        var run = JsTestRunner.Run(TargetTestRunner.Usings + """
            namespace Demo;

            public interface IClientNexus { void Notify(string message); }

            [NexusVersion(Version = "v1")]
            public interface IServerV1 { [NexusMethod(1)] ValueTask<int> Add(int a, int b); }

            [NexusVersion(Version = "v2")]
            public interface IServerV2 : IServerV1 { [NexusMethod(2)] ValueTask Ping(); }

            [Nexus<IServerV2, IClientNexus>(NexusType = NexusType.Server, Targets = NexusTargets.JavaScript)]
            public partial class ChatServerNexus { public ValueTask<int> Add(int a, int b) => new(a + b); public ValueTask Ping() => default; }
            """);
        await Assert.That(run.Diagnostics.Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error).Select(d => d.ToString())).IsEmpty();
        var module = run.File("ChatServerNexus.js");
        await Assert.That(module).Contains("  version: 'v2',\n  serverNexusHash: " + JsSyntax.Int(run.Ir!.Exports.Single().Handshake.ServerNexusHash) + ",\n");
        await Assert.That(module).Contains("server API version `v2`");
    }

    // ------------------------------------------------------------------ proxy

    [Test]
    public async Task ProxyMapsEachReturnKind()
    {
        await Assert.That(Module).Contains("export class ChatServerProxy extends $rt.ProxyBase {");
        await Assert.That(Module).Contains(Lines(2,
            "notify(text) {",
            "  this._invokeVoid(0, ($w) => {",
            "    $w.writeArrayHeader(1);",
            "    $rt.stringFormatter.write($w, text);",
            "  });",
            "}"));
        await Assert.That(Module).Contains("ping() {\n    return this._invoke(1, null);\n  }");
        await Assert.That(Module).Contains("}, { returns: $f1 });");

        await Assert.That(Declarations).Contains("notify(text: string): void;");
        await Assert.That(Declarations).Contains("ping(): Promise<void>;");
        await Assert.That(Declarations).Contains("find(id: number | null, name: string | null): Promise<string | null>;");
        await Assert.That(Declarations).Contains("items(box: Box<number>, map: Map<string, Item | null> | null, color: Color, small: number, money: unknown, array: Array<Item>): Promise<Array<Item>>;");
    }

    [Test]
    public async Task CancellationTokenBecomesTrailingOptions()
    {
        await Assert.That(Module).Contains(Lines(2,
            "add(a, b, options) {",
            "  return this._invoke(2, ($w) => {",
            "    $w.writeArrayHeader(2);",
            "    $rt.int32Formatter.write($w, a);",
            "    $rt.int32Formatter.write($w, b);",
            "  }, { signal: options?.signal, returns: $rt.int32Formatter });",
            "}"));
        await Assert.That(Module).Contains("wait(options) {\n    return this._invoke(9, null, { signal: options?.signal, returns: $rt.int64Formatter });");
        await Assert.That(Declarations).Contains("add(a: number, b: number, options?: { signal?: AbortSignal }): Promise<number>;");

        // A parameter already named options keeps its name; the options take another.
        await Assert.That(Module).Contains("search(options, $options) {");
        await Assert.That(Module).Contains("}, { signal: $options?.signal, returns: $rt.int32Formatter });");
        await Assert.That(Declarations).Contains("search(options: string, $options?: { signal?: AbortSignal }): Promise<number>;");
    }

    [Test]
    public async Task ArgumentsAreAnArrayInWireOrder()
    {
        await Assert.That(Module).Contains(Lines(6,
            "$w.writeArrayHeader(6);",
            "$f2.write($w, box);",
            "$f3.write($w, map);",
            "$fmt.ColorFormatter.write($w, color);",
            "$rt.byteFormatter.write($w, small);",
            "$user0.write($w, money);",
            "$f1.write($w, array);"));
        await Assert.That(Module).Contains("const $f2 = $fmt.BoxFormatter($rt.int32Formatter);");
        await Assert.That(Module).Contains("const $f3 = $rt.mapFormatter($rt.stringFormatter, $fmt.ItemFormatter);");
        await Assert.That(Module).Contains("const $f1 = $rt.arrayFormatter($fmt.ItemFormatter);");
    }

    [Test]
    public async Task PipesAndChannelsAreWrittenAsTheInitialPipeId()
    {
        await Assert.That(Module).Contains(Lines(2,
            "upload(pipe, name) {",
            "  return this._invokePipe(5, pipe, ($w, $pipeId) => {",
            "    $w.writeArrayHeader(2);",
            "    $w.writeInt($pipeId);",
            "    $rt.stringFormatter.write($w, name);",
            "  });",
            "}"));
        await Assert.That(Module).Contains("stream(channel) {\n    return this._invokeChannel(6, channel, ($w, $pipeId) => {");
        await Assert.That(Declarations).Contains("upload(pipe: $rt.NexusDuplexPipe, name: string): Promise<void>;");
        await Assert.That(Declarations).Contains("stream(channel: $rt.NexusDuplexChannel<Item>): Promise<void>;");
    }

    [Test]
    public async Task CollectionsAreGettersWithTheirMode()
    {
        await Assert.That(Module).Contains("get names() {\n    return this._collection(11, $rt.stringFormatter, { mode: $rt.NexusCollectionMode.BiDirectional });");
        await Assert.That(Module).Contains("get list() {\n    return this._collection(12, $fmt.ItemFormatter, { mode: $rt.NexusCollectionMode.ServerToClient });");
        await Assert.That(Module).Contains("get boxes() {\n    return this._collection(13, $f2, { mode: $rt.NexusCollectionMode.Relay });");
        await Assert.That(Declarations).Contains("get names(): $rt.NexusList<string>;");
        await Assert.That(Declarations).Contains("/**\n   * The names in the room.\n");
    }

    [Test]
    public async Task PrimitiveCollectionsPassTheArrayFormatter()
    {
        // C# writes the values of a reset as its T[]: ext 78 for primitive numbers, bin for bytes.
        await Assert.That(Module).Contains("return this._collection(14, $rt.int32Formatter, { mode: $rt.NexusCollectionMode.ServerToClient, valuesFormatter: $rt.int32ArrayFormatter });");
        await Assert.That(Module).Contains("return this._collection(15, $rt.byteFormatter, { mode: $rt.NexusCollectionMode.BiDirectional, valuesFormatter: $rt.binFormatter });");
    }

    [Test]
    public async Task NamesOfReservedMembersAreEscaped()
    {
        await Assert.That(Module).Contains("constructor_() {");
        await Assert.That(Declarations).Contains("constructor_(): Promise<void>;");
        await Assert.That(Module).Contains("invoke(w, pipeId) {");
        await Assert.That(Module).Contains("onConnected_(arguments_, context, methodId) {");
        await Assert.That(Module).Contains("await this.onConnected_(a0, a1, a2);");
        await Assert.That(Declarations).Contains("protected abstract onConnected_(arguments_: number, context: number, methodId: number): void | Promise<void>;");
    }

    // ------------------------------------------------------------------ client nexus

    [Test]
    public async Task ClientNexusHasAnAbstractMethodPerMethod()
    {
        await Assert.That(Module).Contains("export class ChatClientNexusBase extends $rt.ClientNexusBase {");
        await Assert.That(Module).Contains("if (new.target === ChatClientNexusBase) {");
        await Assert.That(Module).Contains("square(value, cancellationToken) {\n    throw new Error('ChatClientNexusBase.square is not implemented.');");
        await Assert.That(Declarations).Contains("export declare abstract class ChatClientNexusBase extends $rt.ClientNexusBase<ChatServerProxy> {");
        await Assert.That(Declarations).Contains("protected abstract square(value: number, cancellationToken: AbortSignal): number | Promise<number>;");
        await Assert.That(Declarations).Contains("protected abstract message(text: string, money: unknown): void;");
        await Assert.That(Declarations).Contains("protected abstract receive(pipe: $rt.NexusDuplexPipe): void | Promise<void>;");
        await Assert.That(Declarations).Contains("protected abstract feed(count: number, channel: $rt.NexusDuplexChannel<Item>): void | Promise<void>;");
        await Assert.That(Declarations).Contains("protected abstract context_(box: Box<string>, item: Item | null): (Array<Item> | null) | Promise<Array<Item> | null>;");
    }

    [Test]
    public async Task DispatchReadsTheArgumentsAndReportsTheResult()
    {
        await Assert.That(Module).Contains(Lines(6,
            "case 0: {",
            "  $r.readArrayHeader();",
            "  const a0 = $rt.int32Formatter.read($r);",
            "  $ctx.writeResult($rt.int32Formatter, await this.square(a0, $ctx.signal));",
            "  return;",
            "}"));
        await Assert.That(Module).Contains("const a1 = $user0.read($r);\n        await this.message(a0, a1);\n        return;");
        await Assert.That(Module).Contains("case 4: {\n        await this.heavy();\n        return;");
        await Assert.That(Module).Contains("default:\n        return super._dispatch($id, $r, $ctx);");
    }

    [Test]
    public async Task PipeMethodsAreAwaitedSoThePipeStaysOpen()
    {
        // Amendment C-3: the runtime releases the pipe when _dispatch completes, which waits for the method.
        await Assert.That(Module).Contains("const a0 = $r.readByte();\n        await this.receive($ctx.pipe(a0));\n        return;");
        await Assert.That(Module).Contains("const a1 = $r.readByte();\n        await this.feed(a0, $ctx.channel(a1, $fmt.ItemFormatter));");
    }

    [Test]
    public async Task SeparatePoolMethodsAreListed()
        => await Assert.That(Module).Contains("_separatePool($id) {\n    return $id === 4 || $id === 5;");

    [Test]
    public async Task NoSeparatePoolOverrideWithoutSuchMethods()
        => await Assert.That(JsEmitterTests.Zoo.File("ZooServerNexus.js")).DoesNotContain("_separatePool");

    [Test]
    public async Task FactoryCreatesTheClient()
    {
        await Assert.That(Module).Contains("export const ChatClientBinding = Object.freeze({\n  handshake: ChatHandshake,\n  proxy: ChatServerProxy,\n});");
        await Assert.That(Module).Contains("export function createChatClient(config, nexus) {\n  return $rt.NexusClient.create(config, nexus, ChatClientBinding);\n}");
        await Assert.That(Declarations).Contains("export declare const ChatClientBinding: $rt.ClientBinding<ChatServerProxy>;");
        await Assert.That(Declarations).Contains("export declare function createChatClient<TNexus extends ChatClientNexusBase>(config: $rt.ClientConfig, nexus: TNexus): $rt.NexusClient<TNexus, ChatServerProxy>;");
    }

    [Test]
    public async Task DocsComeFromTheCSharpDocs()
    {
        await Assert.That(Module).Contains("  /** Sends a message and forgets it. */\n  notify(text) {");
        await Assert.That(Declarations).Contains("   * Returns a + b.\n");
        await Assert.That(Declarations).Contains("   * Returns the square of the value.\n");
        await Assert.That(Declarations).Contains(" * The chat server API & its docs.\n");
    }

    // ------------------------------------------------------------------ target

    [Test]
    public async Task TestScaffoldingModeEmitsNoClientFiles()
    {
        var files = JavaScriptTarget.Instance.Emit(Chat.Ir!, JsTestRunner.TargetOptionsFor(), EmitMode.TestScaffolding);
        await Assert.That(files).IsNotEmpty();
        await Assert.That(files.Select(f => f.Path).Intersect(Chat.Files.Keys)).IsEmpty();
    }

    [Test]
    public async Task ExportsWithoutTheJavaScriptTargetEmitNoModule()
    {
        var ir = Chat.Ir! with { Exports = new(Chat.Ir.Exports.Select(e => e with { Targets = (int)NexusTargets.Java }).ToArray()) };
        var files = JavaScriptTarget.Instance.Emit(ir, JsTestRunner.TargetOptionsFor(), EmitMode.Client);
        await Assert.That(files.Select(f => f.Path)).DoesNotContain("ChatServerNexus.js");
    }

    [Test]
    public async Task TypeNamedLikeAnExportedName_Is041()
    {
        var run = TargetTestRunner.Run(JsTestRunner.GlobalOptions(), TargetTestRunner.Usings + """
            namespace Demo;

            [NexusObject] public class ChatServerProxy { [NexusKey(0)] public int Id { get; set; } }

            public interface IClientNexus { ValueTask Notify(ChatServerProxy message); }
            public interface IServerNexus { ValueTask Ping(); }

            [Nexus<IServerNexus, IClientNexus>(NexusType = NexusType.Server, Targets = NexusTargets.JavaScript)]
            public partial class ChatServerNexus { public ValueTask Ping() => default; }
            """);
        await Assert.That(run.Messages("NEXNET041")).IsEquivalentTo(new[]
        {
            "Name collision (JavaScript): type 'Demo.ChatServerProxy' maps to 'ChatServerProxy', which the module of export 'ChatServerNexus' exports",
        });
    }
}
