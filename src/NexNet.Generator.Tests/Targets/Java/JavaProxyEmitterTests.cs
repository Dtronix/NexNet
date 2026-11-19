using NexNet.Generator.Ir;
using NexNet.Generator.Targets;
using NexNet.Generator.Targets.Java;

namespace NexNet.Generator.Tests.Targets.Java;

/// <summary>The Java proxy and client nexus emitters (K04): text snapshots and structure; no JDK needed.</summary>
public class JavaProxyEmitterTests
{
    private const string Rt = "com.dtronix.nexnet.serialization.Formatters.";

    private static JavaRun? _chat;

    internal static JavaRun Chat => _chat ??= JavaTestRunner.Run(JavaFixtures.ChatPackage, JavaFixtures.Chat);

    private static string Proxy => Chat.File("com/acme/chat/ChatServerProxy.java");

    private static string Nexus => Chat.File("com/acme/chat/ChatClientNexusBase.java");

    /// <summary>The export files of a run (not the types or formatters), path-ordered, with the runtime version masked.</summary>
    internal static string ExportFilesText(JavaRun run, params string[] classes)
        => MaskRuntimeVersion(string.Concat(run.Files
            .Where(f => classes.Any(c => f.Key.EndsWith("/" + c + ".java", StringComparison.Ordinal)))
            .OrderBy(f => f.Key, StringComparer.Ordinal)
            .Select(f => "// ==== " + f.Key + "\n" + f.Value)));

    /// <summary>Snapshots do not change with the NexNet version: the handshake's runtime version is masked.</summary>
    internal static string MaskRuntimeVersion(string text)
        => text.Replace("        " + JavaSyntax.StringLiteral(ProxyEmitter.RuntimeVersion) + ",\n", "        \"<runtime-version>\",\n")
            .Replace("runtime " + ProxyEmitter.RuntimeVersion + ",", "runtime <runtime-version>,");

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
        => JavaSnapshots.Verify("chat", ExportFilesText(Chat, "ChatHandshake", "ChatServerProxy", "ChatClientNexusBase", "ChatClient"));

    [Test]
    public async Task ExportFilesAreTheEmittedFiles()
    {
        var options = JavaTestRunner.TargetOptionsFor(JavaFixtures.ChatPackage);
        var export = Chat.Ir!.Exports.Single();
        var expected = new[]
        {
            "com/acme/chat/ChatHandshake.java", "com/acme/chat/ChatServerProxy.java",
            "com/acme/chat/ChatClientNexusBase.java", "com/acme/chat/ChatClient.java",
        };
        await Assert.That(JavaTarget.Instance.ExportFiles(export, options)).IsEquivalentTo(expected);
        foreach (var path in expected)
            await Assert.That(Chat.Files.Keys).Contains(path);

        var typeFiles = Chat.Ir.Types.SelectMany(t => JavaTarget.Instance.TypeFiles(t, options));
        var others = Chat.Files.Keys.Except(expected).Except(typeFiles).Where(p => !p.StartsWith("com/acme/chat/formatters/", StringComparison.Ordinal));
        await Assert.That(others).IsEmpty();
    }

    [Test]
    [Arguments("ChatServerNexus", "Chat")]
    [Arguments("ChatNexus", "Chat")]
    [Arguments("Chat", "Chat")]
    [Arguments("ServerNexus", "Server")]
    [Arguments("Nexus", "Nexus")]
    [Arguments("InteropVersionedServerNexusV1", "InteropVersionedServerNexusV1")]
    public async Task ClassNamesComeFromTheExportName(string export, string stem)
        => await Assert.That(JavaExportNames.Stem(export)).IsEqualTo(stem);

    [Test]
    public async Task OutputIsDeterministic()
    {
        var again = JavaTestRunner.Run(JavaFixtures.ChatPackage, JavaFixtures.Chat);
        await Assert.That(again.Concatenated()).IsEqualTo(Chat.Concatenated());
        await Assert.That(Chat.Concatenated()).DoesNotContain("\r");
    }

    [Test]
    public async Task RuntimeVersionIsTheGeneratorVersion()
    {
        var version = typeof(JavaTarget).Assembly.GetName().Version!;
        await Assert.That(ProxyEmitter.RuntimeVersion).IsEqualTo(version.Major + "." + version.Minor + "." + version.Build);
        await Assert.That(ProxyEmitter.RuntimeVersion).IsEqualTo(typeof(NexNet.NexusTargets).Assembly.GetName().Version!.ToString(3));
    }

    // ------------------------------------------------------------------ handshake

    [Test]
    public async Task HandshakeHoldsTheIrConstants()
    {
        var export = Chat.Ir!.Exports.Single();
        var handshake = Chat.File("com/acme/chat/ChatHandshake.java");
        await Assert.That(handshake).Contains("public static final HandshakeDescriptor DESCRIPTOR = new HandshakeDescriptor(\n"
                                              + "        \"" + ProxyEmitter.RuntimeVersion + "\",\n"
                                              + "        null,\n"
                                              + "        " + JavaSyntax.Int(export.Handshake.ServerNexusHash) + ",\n"
                                              + "        " + JavaSyntax.Int(export.Handshake.ClientNexusHash) + ",\n"
                                              + "        new int[] {" + string.Join(", ", export.ClientAcceptedIds.Select(a => a.Id)) + "});");
    }

    [Test]
    public async Task VersionedHandshakeSendsTheVersion()
    {
        var run = JavaTestRunner.Run("com.acme", TargetTestRunner.Usings + """
            namespace Demo;

            public interface IClientNexus { void Notify(string message); }

            [NexusVersion(Version = "v1")]
            public interface IServerV1 { [NexusMethod(1)] ValueTask<int> Add(int a, int b); }

            [NexusVersion(Version = "v2")]
            public interface IServerV2 : IServerV1 { [NexusMethod(2)] ValueTask Ping(); }

            [Nexus<IServerV2, IClientNexus>(NexusType = NexusType.Server, Targets = NexusTargets.Java)]
            public partial class ChatServerNexus { public ValueTask<int> Add(int a, int b) => new(a + b); public ValueTask Ping() => default; }
            """);
        await Assert.That(run.Diagnostics.Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error).Select(d => d.ToString())).IsEmpty();
        var handshake = run.File("com/acme/ChatHandshake.java");
        await Assert.That(handshake).Contains("        \"v2\",\n        " + JavaSyntax.Int(run.Ir!.Exports.Single().Handshake.ServerNexusHash) + ",\n");
        await Assert.That(handshake).Contains("server API version {@code v2}");
    }

    // ------------------------------------------------------------------ proxy

    [Test]
    public async Task ProxyMapsEachReturnKind()
    {
        await Assert.That(Proxy).Contains("public final class ChatServerProxy extends ProxyBase {");
        await Assert.That(Proxy).Contains("public ChatServerProxy(ProxyInvoker invoker) {");
        await Assert.That(Proxy).Contains("public void notify_(String text) {");
        await Assert.That(Proxy).Contains("invokeVoid(0, arguments$);");
        await Assert.That(Proxy).Contains("public CompletableFuture<Void> ping() {\n        return invokeTask(1, ArgumentWriter.NONE, null);");
        await Assert.That(Proxy).Contains("public CompletableFuture<java.util.List<com.acme.chat.Item>> items(");
        await Assert.That(Proxy).Contains("return invoke(4, arguments$, F$3, null);");
    }

    [Test]
    public async Task CancellationTokenMethodsHaveTwoOverloads()
    {
        await Assert.That(Proxy).Contains("public CompletableFuture<Integer> add(int a, int b) {\n        return add(a, b, CancellationToken.NONE);");
        await Assert.That(Proxy).Contains("public CompletableFuture<Integer> add(int a, int b, CancellationToken cancellationToken) {");
        await Assert.That(Proxy).Contains("return invoke(2, arguments$, " + Rt + "INT, cancellationToken);");
        await Assert.That(Proxy).Contains("public CompletableFuture<Long> wait_(CancellationToken token) {");
        await Assert.That(Proxy).Contains("public CompletableFuture<Long> wait_() {\n        return wait_(CancellationToken.NONE);");
    }

    [Test]
    public async Task ArgumentsAreAnArrayInWireOrder()
    {
        await Assert.That(Proxy).Contains(Lines(12,
            "writer$.writeArrayHeader(6);",
            "F$1.write(writer$, box);",
            "F$2.write(writer$, map);",
            "com.acme.chat.formatters.Formatters.Color.write(writer$, color);",
            "com.dtronix.nexnet.serialization.Formatters.BYTE.write(writer$, small);",
            "com.acme.chat.formatters.UserFormatters.com_acme_ext_MoneyFormatter.write(writer$, money);",
            "F$3.write(writer$, array);"));
        await Assert.That(Proxy).Contains("public CompletableFuture<@Nullable String> find(@Nullable Integer id, @Nullable String name) {");
        await Assert.That(Proxy).Contains("private static final Formatter<Integer> F$0 =\n        " + Rt + "nullable(" + Rt + "INT);");
    }

    [Test]
    public async Task PipesAndChannelsAreWrittenAsTheInitialPipeId()
    {
        await Assert.That(Proxy).Contains("""
                public CompletableFuture<Void> upload(NexusDuplexPipe pipe, String name) {
                    return invokePipe(5, pipe, (writer$, pipeId$) -> {
                        writer$.writeArrayHeader(2);
                        writer$.writeInt(pipeId$);
            """.Replace("\r\n", "\n"));
        await Assert.That(Proxy).Contains("public CompletableFuture<Void> stream(NexusDuplexChannel<com.acme.chat.Item> channel) {\n        return invokeChannel(6, channel, (writer$, pipeId$) -> {");
    }

    [Test]
    public async Task CollectionsPassTheirMode()
    {
        await Assert.That(Proxy).Contains("public NexusList<String> names() {\n        return list(10, NexusCollectionMode.BI_DIRECTIONAL, " + Rt + "STRING);");
        await Assert.That(Proxy).Contains("public NexusList<com.acme.chat.Item> list_() {\n        return list(11, NexusCollectionMode.SERVER_TO_CLIENT, com.acme.chat.formatters.Formatters.Item);");
        await Assert.That(Proxy).Contains("public NexusList<com.acme.chat.Box<Integer>> boxes() {\n        return list(12, NexusCollectionMode.RELAY, F$1);");
        await Assert.That(Proxy).Contains("/**\n     * The names in the room.\n");
    }

    [Test]
    public async Task NamesOfBaseMembersAndJavaObjectMethodsAreEscaped()
    {
        await Assert.That(Proxy).Contains("public CompletableFuture<com.acme.chat.@Nullable Item> invoke_(int w, int pipeId) {");
        await Assert.That(Proxy).Contains("public CompletableFuture<Void> hashCode_() {");
        await Assert.That(Nexus).Contains("protected abstract CompletableFuture<java.util.@Nullable List<com.acme.chat.Item>> context_(com.acme.chat.Box<String> box, com.acme.chat.@Nullable Item item);");
        await Assert.That(Nexus).Contains("protected abstract CompletableFuture<Void> dispatch_(int arguments, int context, int methodId);");
    }

    // ------------------------------------------------------------------ client nexus

    [Test]
    public async Task ClientNexusHasAnAbstractMethodPerMethod()
    {
        await Assert.That(Nexus).Contains("public abstract class ChatClientNexusBase extends ClientNexusBase<ChatServerProxy> {");
        await Assert.That(Nexus).Contains("protected abstract CompletableFuture<Integer> square(int value, CancellationToken cancellationToken);");
        await Assert.That(Nexus).Contains("protected abstract void message(String text, Object money);");
        await Assert.That(Nexus).Contains("protected abstract CompletableFuture<Void> receive(NexusDuplexPipe pipe);");
        await Assert.That(Nexus).Contains("protected abstract CompletableFuture<Void> feed(int count, NexusDuplexChannel<com.acme.chat.Item> channel);");
        await Assert.That(Nexus).Contains("protected abstract CompletableFuture<com.acme.chat.Color> pick(java.util.List<com.acme.chat.Color> colors, java.util.@Nullable Map<Integer, String> map);");
    }

    [Test]
    public async Task DispatchReadsTheArgumentsAndReturnsThroughTheContext()
    {
        await Assert.That(Nexus).Contains(Lines(12,
            "case 0: {",
            "    arguments.readArrayHeader();",
            "    int a0 = com.dtronix.nexnet.serialization.Formatters.INT.read(arguments);",
            "    return context.result(square(a0, context.cancellationToken()), com.dtronix.nexnet.serialization.Formatters.INT);",
            "}"));
        await Assert.That(Nexus).Contains("message(a0, a1);\n                return context.noResult();");
        await Assert.That(Nexus).Contains("case 4: {\n                return context.result(heavy());");
        await Assert.That(Nexus).Contains("throw new IllegalArgumentException(\"Unknown method id \" + methodId);");
    }

    [Test]
    public async Task PipeMethodsReturnTheirFutureThroughTheContext()
    {
        // Amendment C-3: the runtime closes the pipe when the method's future completes.
        await Assert.That(Nexus).Contains("int a0 = arguments.readInt();\n                return context.result(receive(context.pipe(a0)));");
        await Assert.That(Nexus).Contains("int a1 = arguments.readInt();\n                return context.result(feed(a0, context.channel(a1, com.acme.chat.formatters.Formatters.Item)));");
        await Assert.That(Nexus).DoesNotContain("receive(context.pipe(a0));\n                return context.noResult();");
    }

    [Test]
    public async Task SeparatePoolMethodsAreListed()
    {
        await Assert.That(Nexus).Contains("protected boolean separatePool(int methodId) {\n        return methodId == 4 || methodId == 5;");
        await Assert.That(Chat.File("com/acme/chat/ChatServerProxy.java")).DoesNotContain("separatePool");
    }

    [Test]
    public async Task NoSeparatePoolOverrideWithoutSuchMethods()
    {
        var zoo = JavaEmitterTests.Zoo.Files.Single(f => f.Key.EndsWith("ClientNexusBase.java", StringComparison.Ordinal)).Value;
        await Assert.That(zoo).DoesNotContain("separatePool");
    }

    [Test]
    public async Task FactoryCreatesTheClient()
    {
        var factory = Chat.File("com/acme/chat/ChatClient.java");
        await Assert.That(factory).Contains("public static NexusClient<ChatServerProxy> create(ClientConfig config, ChatClientNexusBase nexus) {\n"
                                            + "        return NexusClient.create(config, ChatHandshake.DESCRIPTOR, nexus, ChatServerProxy::new);");
        await Assert.That(factory).Contains("ChatServerProxy::new, connector);");
    }

    [Test]
    public async Task JavadocComesFromTheCSharpDocs()
    {
        await Assert.That(Proxy).Contains("/** Sends a message and forgets it. */\n    public void notify_(String text) {");
        await Assert.That(Proxy).Contains("     * Returns a + b.\n");
        await Assert.That(Nexus).Contains("     * Returns the square of the value.\n");
        await Assert.That(Chat.File("com/acme/chat/ChatClient.java")).Contains(" * The chat server API &amp; its docs.\n");
    }

    [Test]
    public async Task TestScaffoldingModeEmitsNoClientFiles()
    {
        var files = JavaTarget.Instance.Emit(Chat.Ir!, JavaTestRunner.TargetOptionsFor(JavaFixtures.ChatPackage), EmitMode.TestScaffolding);
        await Assert.That(files.Select(f => f.Path).Intersect(Chat.Files.Keys)).IsEmpty();
    }

    [Test]
    public async Task ExportsWithoutTheJavaTargetEmitNoProxy()
    {
        var ir = Chat.Ir! with { Exports = new(Chat.Ir.Exports.Select(e => e with { Targets = (int)NexusTargets.JavaScript }).ToArray()) };
        var files = JavaTarget.Instance.Emit(ir, JavaTestRunner.TargetOptionsFor(JavaFixtures.ChatPackage), EmitMode.Client);
        await Assert.That(files.Select(f => f.Path)).DoesNotContain("com/acme/chat/ChatServerProxy.java");
    }
}
