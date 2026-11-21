using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using NexNet.Generator.Targets.Java;

namespace NexNet.Generator.Tests.Targets.Java;

/// <summary>
/// SDK-gated checks of the Java test scaffolding (K11, D27): <c>javac --release 11 -Xlint:all -Werror</c> of the client
/// and scaffolding code against the runtime jar, and a runner that drives the interop nexus's scaffolding: the golden
/// vectors through the typed-JSON codecs and echo invocations by name, and the scriptable client nexus through a fake
/// script host. Skipped without <c>NEXNET_REQUIRE_SDKS=1</c> or <c>NEXNET_TEST_LANGS=java</c>.
/// </summary>
public class JavaScaffoldingCompileTests
{
    private const int VectorsPerMethod = 40;

    /// <summary>Vectors whose value the Java type cannot hold, with the reason.</summary>
    private static readonly Dictionary<string, string> Unrepresentable = new(StringComparer.Ordinal)
    {
        // Java maps decimal to BigDecimal, which has no negative zero: -0m reads as 0 (the sign bit is lost). A known
        // parity loss (K31; docs/internals/java-runtime-contract.md section 5). The runtime's golden tests cover the
        // vector through NexusDecimal, which keeps the sign.
        ["decimal.m0"] = "known parity loss (K31): BigDecimal has no negative zero, so -0m reads and re-encodes as 0m",
    };

    [Test]
    public async Task ChatScaffoldingCompilesWithJavac()
    {
        var sdk = await JavaSdk.Get();
        using var dir = new ScaffoldTempDir();
        var result = await Compile(sdk, dir, JavaProxyEmitterTests.Chat, JavaScaffoldingTests.Chat, ChatSupport);
        await Assert.That(result.Success).IsTrue().Because("javac failed:\n" + result.Output);
        await Assert.That(result.Output.Trim()).IsEmpty().Because("javac printed:\n" + result.Output);
    }

    [Test]
    public async Task InteropScaffoldingRunsAgainstTheRuntime()
    {
        var sdk = await JavaSdk.Get();
        using var dir = new ScaffoldTempDir();
        var support = new Dictionary<string, string>(InteropSupport) { ["k11/runner/Runner.java"] = Runner(out var vectors) };
        await Assert.That(vectors).IsGreaterThan(100);

        var result = await Compile(sdk, dir, JavaScaffoldingTests.Interop, JavaScaffoldingTests.InteropScaffolding, support);
        await Assert.That(result.Success).IsTrue().Because("javac failed:\n" + result.Output);
        await Assert.That(result.Output.Trim()).IsEmpty().Because("javac printed:\n" + result.Output);

        var run = await sdk.RunMain(dir.Classes, "k11.runner.Runner");
        await Assert.That(run.Success).IsTrue().Because("The runner failed:\n" + run.Output);
        await Assert.That(run.Output).Contains("failed=0");

        var counts = Regex.Match(run.Output, @"vectors=(\d+) echoes=(\d+)");
        await Assert.That(counts.Success).IsTrue().Because(run.Output);
        await Assert.That(int.Parse(counts.Groups[1].Value, CultureInfo.InvariantCulture)).IsGreaterThan(100).Because(run.Output);
        await Assert.That(int.Parse(counts.Groups[2].Value, CultureInfo.InvariantCulture)).IsGreaterThan(50).Because(run.Output);
    }

    // ------------------------------------------------------------------ plumbing

    private static async Task<ProcessResult> Compile(JavaSdk sdk, ScaffoldTempDir dir, JavaRun client, ScaffoldRun scaffolding, IReadOnlyDictionary<string, string> support)
    {
        await Assert.That(client.Diagnostics.Where(d => d.Id != "NEXNET044").Select(d => d.ToString())).IsEmpty();
        var sources = new List<string>();
        foreach (var (path, content) in client.Files.Concat(scaffolding.Files).Concat(support))
        {
            var full = Path.Combine(dir.Source, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            await File.WriteAllTextAsync(full, content);
            sources.Add(path);
        }

        return await sdk.Compile(dir.Source, sources, dir.Classes);
    }

    private static readonly Dictionary<string, string> ChatSupport = new()
    {
        ["com/acme/ext/MoneyFormatter.java"] = """
            package com.acme.ext;

            import com.dtronix.nexnet.serialization.Formatter;
            import com.dtronix.nexnet.serialization.MsgPackReader;
            import com.dtronix.nexnet.serialization.MsgPackWriter;

            /** Test formatter of the C# Money struct: its cents. */
            public final class MoneyFormatter implements Formatter<Long> {
                /** Creates the formatter. */
                public MoneyFormatter() {
                }

                @Override
                public void write(MsgPackWriter writer, Long value) {
                    writer.writeInt(value == null ? 0L : value);
                }

                @Override
                public Long read(MsgPackReader reader) {
                    return reader.readLong();
                }
            }
            """,
    };

    private static readonly Dictionary<string, string> InteropSupport = new()
    {
        ["com/dtronix/nexnet/testnexus/InteropPointFormatter.java"] = """
            package com.dtronix.nexnet.testnexus;

            import com.dtronix.nexnet.serialization.Formatter;
            import com.dtronix.nexnet.serialization.MsgPackReader;
            import com.dtronix.nexnet.serialization.MsgPackWriter;
            import java.util.Arrays;
            import java.util.List;

            /** Test formatter of the C# InteropPoint: [X, Y]. */
            public final class InteropPointFormatter implements Formatter<List<Integer>> {
                /** Creates the formatter. */
                public InteropPointFormatter() {
                }

                @Override
                public void write(MsgPackWriter writer, List<Integer> value) {
                    writer.writeArrayHeader(2);
                    writer.writeInt(value.get(0));
                    writer.writeInt(value.get(1));
                }

                @Override
                public List<Integer> read(MsgPackReader reader) {
                    reader.readArrayHeader(2);
                    int x = reader.readInt();
                    return Arrays.asList(x, reader.readInt());
                }
            }
            """,
    };

    /// <summary>The runner, with the client method ids and the golden vectors of the interop types filled in.</summary>
    private static string Runner(out int vectors)
    {
        var ir = JavaScaffoldingTests.Interop.Ir!;
        var export = ir.Exports.Single(e => e.ServerApi.ClrName == "NexNet.TestNexus.Interop.IInteropServerNexus");
        var text = RunnerTemplate.Replace("\r\n", "\n");
        foreach (var method in export.ClientApi.Methods)
            text = text.Replace("$ID_" + method.Name + ",", method.Id.ToString(CultureInfo.InvariantCulture) + ",");
        text = text.Replace("$EXPORTS", ir.Exports.Count(e => (e.Targets & 1) != 0).ToString(CultureInfo.InvariantCulture));

        // Golden type expressions name declared types by simple name; the runner's (and TestCodecs') use the id.
        var ids = ir.Types.ToDictionary(t => t.Name, t => t.Id, StringComparer.Ordinal);
        var calls = new List<string>();
        var golden = Path.Combine(JavaTestRunner.RepoRoot(), "tests", "golden");
        foreach (var name in new[] { "primitives.jsonl", "containers.jsonl", "ext78.jsonl", "objects.jsonl", "unions.jsonl" })
        {
            foreach (var line in File.ReadAllLines(Path.Combine(golden, name)))
            {
                if (line.Trim().Length == 0)
                    continue;
                using var json = JsonDocument.Parse(line);
                var root = json.RootElement;
                var mode = root.GetProperty("mode").GetString();
                if (mode == "reject" || root.TryGetProperty("options", out _) || Unrepresentable.ContainsKey(root.GetProperty("id").GetString()!))
                    continue;
                var hex = root.GetProperty("hex").GetString()!;
                var value = root.GetProperty("value");
                if (hex.Length > 4000 || value.GetRawText().Length > 2000)
                    continue;

                var type = Regex.Replace(root.GetProperty("type").GetString()!, "[A-Za-z_][A-Za-z0-9_]*", m => ids.TryGetValue(m.Value, out var id) ? id : m.Value);
                calls.Add("vector(" + JavaSyntax.StringLiteral(root.GetProperty("id").GetString()!) + ", " + JavaSyntax.StringLiteral(type) + ", "
                          + JavaSyntax.StringLiteral(hex) + ", " + Java(value) + ", " + (mode == "both" ? "true" : "false") + ");");
            }
        }

        vectors = calls.Count;
        var invocations = new StringBuilder();
        var chunks = new StringBuilder();
        for (var i = 0; i * VectorsPerMethod < calls.Count; i++)
        {
            var index = i.ToString(CultureInfo.InvariantCulture);
            invocations.Append("        v").Append(index).Append("();\n");
            chunks.Append("\n    private static void v").Append(index).Append("() {\n");
            foreach (var call in calls.Skip(i * VectorsPerMethod).Take(VectorsPerMethod))
                chunks.Append("        ").Append(call).Append('\n');
            chunks.Append("    }\n");
        }

        return text.Replace("$VECTORS\n", invocations.ToString()).Replace("$CHUNKS\n", chunks.ToString());
    }

    /// <summary>A Java expression of a typed-JSON value: Long, String, Boolean, null, nested lists.</summary>
    private static string Java(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null => "null",
        JsonValueKind.True => "Boolean.TRUE",
        JsonValueKind.False => "Boolean.FALSE",
        JsonValueKind.Number => value.GetInt64().ToString(CultureInfo.InvariantCulture) + "L",
        JsonValueKind.String => JavaSyntax.StringLiteral(value.GetString()!),
        JsonValueKind.Array => value.GetArrayLength() == 1 && value[0].ValueKind == JsonValueKind.Null
            ? "A((Object) null)"
            : "A(" + string.Join(", ", value.EnumerateArray().Select(Java)) + ")",
        _ => throw new InvalidOperationException("Unexpected typed JSON " + value.GetRawText()),
    };

    private const string RunnerTemplate = """
package k11.runner;

import com.dtronix.nexnet.CancellationToken;
import com.dtronix.nexnet.client.ClientConfig;
import com.dtronix.nexnet.client.NexusClient;
import com.dtronix.nexnet.collections.NexusCollectionMode;
import com.dtronix.nexnet.collections.NexusList;
import com.dtronix.nexnet.pipes.NexusDuplexChannel;
import com.dtronix.nexnet.pipes.NexusDuplexPipe;
import com.dtronix.nexnet.serialization.Formatter;
import com.dtronix.nexnet.serialization.Formatters;
import com.dtronix.nexnet.serialization.MsgPackOptions;
import com.dtronix.nexnet.serialization.MsgPackReader;
import com.dtronix.nexnet.serialization.MsgPackWriter;
import com.dtronix.nexnet.spi.ArgumentWriter;
import com.dtronix.nexnet.spi.ClientNexusBase;
import com.dtronix.nexnet.spi.ClientNexusHooks;
import com.dtronix.nexnet.spi.InvocationContext;
import com.dtronix.nexnet.spi.PipeArgumentWriter;
import com.dtronix.nexnet.spi.ProxyInvoker;
import com.dtronix.nexnet.testnexus.InteropServerProxy;
import com.dtronix.nexnet.testnexus.scaffolding.InteropScaffolding;
import com.dtronix.nexnet.testnexus.scaffolding.Scaffolding;
import com.dtronix.nexnet.testnexus.scaffolding.ScaffoldingExport;
import com.dtronix.nexnet.testnexus.scaffolding.ScriptCall;
import com.dtronix.nexnet.testnexus.scaffolding.ScriptHost;
import com.dtronix.nexnet.testnexus.scaffolding.TestCodecs;
import com.dtronix.nexnet.testnexus.scaffolding.TypedCodec;
import com.dtronix.nexnet.testnexus.scaffolding.TypedJson;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.HashMap;
import java.util.List;
import java.util.Map;
import java.util.Objects;
import java.util.concurrent.CompletableFuture;

/** Runs the generated test scaffolding of the interop nexus against the runtime. */
public final class Runner {
    private static final CancellationToken TOKEN = new com.dtronix.nexnet.CancellationTokenSource().token();
    private static final Map<String, ScaffoldingExport.Method> ECHO = new HashMap<>();
    private static final InteropServerProxy PROXY = new InteropServerProxy(new EchoInvoker());
    private static int checkedCount;
    private static int failedCount;
    private static int vectorCount;
    private static int echoCount;

    private Runner() {
    }

    static List<Object> A(Object... items) {
        return Arrays.asList(items);
    }

    private static byte[] bytes(String hex) {
        byte[] result = new byte[hex.length() / 2];
        for (int i = 0; i < result.length; i++) {
            result[i] = (byte) Integer.parseInt(hex.substring(2 * i, 2 * i + 2), 16);
        }
        return result;
    }

    private static String hex(byte[] bytes) {
        StringBuilder sb = new StringBuilder();
        for (byte b : bytes) {
            sb.append(String.format("%02x", b & 0xFF));
        }
        return sb.toString();
    }

    private static void check(boolean ok, String what) {
        checkedCount++;
        if (!ok) {
            failedCount++;
            System.out.println("FAIL " + what);
        }
    }

    private static <T> Object decode(TypedCodec<T> codec, byte[] data) {
        T value = Formatters.deserialize(codec.formatter(), data, MsgPackOptions.TRUSTED);
        return codec.toJson(value);
    }

    private static <T> byte[] encode(TypedCodec<T> codec, Object json) {
        return Formatters.serialize(codec.formatter(), codec.fromJson(json));
    }

    /** A golden vector: decoding gives the value, encoding the value gives the bytes, an echo returns it. */
    static void vector(String id, String expression, String hexText, Object value, boolean both) {
        TypedCodec<?> codec = TestCodecs.forType(expression);
        if (codec == null) {
            return;
        }
        vectorCount++;
        try {
            Object decoded = decode(codec, bytes(hexText));
            check(Objects.equals(value, decoded), id + ": decoded " + decoded + ", expected " + value);
            if (both) {
                String encoded = hex(encode(codec, value));
                check(encoded.equals(hexText), id + ": encoded " + encoded + ", expected " + hexText);
            }
            ScaffoldingExport.Method echo = ECHO.get(expression);
            if (echo != null) {
                echoCount++;
                Object result = InteropScaffolding.INSTANCE.invokeByName(PROXY, echo.name(), A(value), null, CancellationToken.NONE).join();
                check(Objects.equals(value, result), id + ": " + echo.name() + " returned " + result);
            }
        } catch (RuntimeException e) {
            check(false, id + ": " + e);
        }
    }

    /** Echoes the first argument back through the result formatter, as the interop server's Echo methods do. */
    private static final class EchoInvoker implements ProxyInvoker {
        @Override
        public <R> CompletableFuture<R> invoke(int methodId, ArgumentWriter arguments, Formatter<R> result,
                                               CancellationToken cancellationToken) {
            MsgPackWriter writer = new MsgPackWriter();
            arguments.write(writer);
            MsgPackReader reader = new MsgPackReader(writer.toByteArray());
            reader.readArrayHeader();
            return CompletableFuture.completedFuture(result.read(reader));
        }

        @Override
        public CompletableFuture<Void> invokeTask(int methodId, ArgumentWriter arguments, CancellationToken cancellationToken) {
            return CompletableFuture.completedFuture(null);
        }

        @Override
        public void invokeVoid(int methodId, ArgumentWriter arguments) {
        }

        @Override
        public CompletableFuture<Void> invokePipe(int methodId, NexusDuplexPipe pipe, PipeArgumentWriter arguments) {
            return CompletableFuture.completedFuture(null);
        }

        @Override
        public <T> CompletableFuture<Void> invokeChannel(int methodId, NexusDuplexChannel<T> channel, PipeArgumentWriter arguments) {
            return CompletableFuture.completedFuture(null);
        }

        @Override
        public <T> NexusList<T> list(int collectionId, NexusCollectionMode mode, Formatter<T> itemFormatter) {
            throw new UnsupportedOperationException("list");
        }
    }

    /** Records calls and answers with a programmed result. */
    private static final class Host implements ScriptHost {
        private final List<ScriptCall> calls = new ArrayList<>();
        private Object reply;

        @Override
        public CompletableFuture<Object> call(ScriptCall call) {
            calls.add(call);
            return CompletableFuture.completedFuture(reply);
        }

        ScriptCall last() {
            return calls.get(calls.size() - 1);
        }
    }

    /** Captures what the dispatch hands back. */
    private static final class Context implements InvocationContext {
        private String result = "(none)";

        @Override
        public CancellationToken cancellationToken() {
            return TOKEN;
        }

        @Override
        public NexusDuplexPipe pipe(int initialPipeId) {
            return null;
        }

        @Override
        public <T> NexusDuplexChannel<T> channel(int initialPipeId, Formatter<T> itemFormatter) {
            return null;
        }

        @Override
        public <R> CompletableFuture<Void> result(CompletableFuture<R> future, Formatter<R> formatter) {
            result = hex(Formatters.serialize(formatter, future.join()));
            return CompletableFuture.completedFuture(null);
        }

        @Override
        public CompletableFuture<Void> result(CompletableFuture<Void> future) {
            future.join();
            result = "done";
            return CompletableFuture.completedFuture(null);
        }

        @Override
        public CompletableFuture<Void> noResult() {
            result = "none";
            return CompletableFuture.completedFuture(null);
        }
    }

    private static String dispatch(ClientNexusBase<?> nexus, int methodId, MsgPackWriter arguments) {
        Context context = new Context();
        ClientNexusHooks.dispatch(nexus, methodId, new MsgPackReader(arguments.toByteArray()), context).join();
        return context.result;
    }

    private static void scriptableNexus() {
        Host host = new Host();
        ClientNexusBase<InteropServerProxy> nexus = InteropScaffolding.INSTANCE.createNexus(host);

        MsgPackWriter args = new MsgPackWriter();
        args.writeArrayHeader(1);
        args.writeString("hi");
        host.reply = "yo";
        String result = dispatch(nexus, $ID_ClientEchoString, args);
        check(host.last().slot().equals("ClientEchoString") && host.last().arguments().equals(A("hi")), "ClientEchoString call " + host.last());
        check(host.last().cancellationToken() == CancellationToken.NONE, "ClientEchoString token");
        check(result.equals("a2796f"), "ClientEchoString result " + result);

        args = new MsgPackWriter();
        args.writeArrayHeader(2);
        args.writeInt(5);
        args.writeInt(7);
        host.reply = null;
        result = dispatch(nexus, $ID_ClientDelay, args);
        check(host.last().slot().equals("ClientDelay") && host.last().arguments().equals(A(5L, 7L)), "ClientDelay call " + host.last());
        check(host.last().cancellationToken() == TOKEN, "ClientDelay token");
        check(result.equals("00"), "ClientDelay default result " + result);

        args = new MsgPackWriter();
        args.writeArrayHeader(1);
        args.writeString("note");
        result = dispatch(nexus, $ID_ClientNotify, args);
        check(host.last().slot().equals("ClientNotify") && result.equals("none"), "ClientNotify " + host.last() + " " + result);

        args = new MsgPackWriter();
        args.writeArrayHeader(1);
        args.writeInt(3);
        result = dispatch(nexus, $ID_ClientEchoPipe, args);
        check(host.last().slot().equals("ClientEchoPipe") && host.last().arguments().isEmpty() && result.equals("done"), "ClientEchoPipe " + result);

        host.reply = A(1L, A("f64:0x4008000000000000", "f64:0x4010000000000000"));
        args = new MsgPackWriter();
        args.writeArrayHeader(1);
        args.writeNil();
        result = dispatch(nexus, $ID_ClientEchoShape, args);
        check(host.last().arguments().equals(Arrays.asList((Object) null)), "ClientEchoShape null argument " + host.last());
        check(result.equals("920192cb4008000000000000cb4010000000000000"), "ClientEchoShape result " + result);

        ClientNexusHooks.connected(nexus, true).join();
        check(host.last().slot().equals(ScriptCall.ON_CONNECTED) && host.last().arguments().equals(A(Boolean.TRUE)), "OnConnected " + host.last());
        ClientNexusHooks.disconnected(nexus).join();
        check(host.last().slot().equals(ScriptCall.ON_DISCONNECTED) && host.last().arguments().isEmpty(), "OnDisconnected " + host.last());

        check(InteropScaffolding.INSTANCE.slots().contains("ClientEchoString")
              && InteropScaffolding.INSTANCE.slots().contains(ScriptCall.ON_CONNECTED), "slots " + InteropScaffolding.INSTANCE.slots());
    }

    private static void exports() {
        check(Scaffolding.SCHEMA_HASH.matches("[0-9a-f]{64}"), "schema hash " + Scaffolding.SCHEMA_HASH);
        check(Scaffolding.find("NexNet.TestNexus.Interop.IInteropClientNexus", "NexNet.TestNexus.Interop.IInteropServerNexus")
              == InteropScaffolding.INSTANCE, "find");
        check(Scaffolding.find("x", "y") == null, "find unknown");
        check(Scaffolding.EXPORTS.size() == $EXPORTS, "exports " + Scaffolding.EXPORTS);
        check(InteropScaffolding.INSTANCE.method("EchoInt64Channel").channelItem() == TestCodecs.forType("long"), "channel item codec");
        check(InteropScaffolding.INSTANCE.method("Delay").cancellable(), "Delay cancellable");
        check(InteropScaffolding.INSTANCE.method("Notify").returnKind() == ScaffoldingExport.ReturnKind.VOID, "Notify void");
        check(InteropScaffolding.INSTANCE.collection("Items").item() == TestCodecs.forType("NexNet.TestNexus.Interop.InteropItem"), "Items codec");

        Object result = InteropScaffolding.INSTANCE.invokeUnchecked(PROXY, "Delay", A(10L, 42L), null, TOKEN).join();
        check(Objects.equals(result, 10L), "Delay echo " + result);
        result = InteropScaffolding.INSTANCE.invokeByName(PROXY, "Notify", A("x"), null, CancellationToken.NONE).join();
        check(result == null, "Notify result " + result);
        result = InteropScaffolding.INSTANCE.invokeByName(PROXY, "Ping", A(), null, CancellationToken.NONE).join();
        check(result == null, "Ping result " + result);
        result = InteropScaffolding.INSTANCE.invokeByName(PROXY, "EchoStruct", A((Object) null), null, CancellationToken.NONE).join();
        check(Objects.equals(result, TypedJson.defaultValue(TestCodecs.forType("NexNet.TestNexus.Interop.InteropStruct").type(), TestCodecs.SCHEMAS)),
              "EchoStruct default " + result);
        try {
            InteropScaffolding.INSTANCE.invokeByName(PROXY, "Nope", A(), null, CancellationToken.NONE);
            check(false, "unknown method accepted");
        } catch (IllegalArgumentException expected) {
            check(true, "unknown method");
        }
        try {
            InteropScaffolding.INSTANCE.invokeByName(PROXY, "EchoInt32", A(), null, CancellationToken.NONE);
            check(false, "missing argument accepted");
        } catch (IllegalArgumentException expected) {
            check(true, "missing argument");
        }
        try {
            InteropScaffolding.INSTANCE.invokeByName(PROXY, "EchoByte", A(256L), null, CancellationToken.NONE);
            check(false, "byte 256 accepted");
        } catch (IllegalArgumentException expected) {
            check(true, "byte range");
        }
        try {
            InteropScaffolding.INSTANCE.invokeByName(PROXY, "EchoPipe", A(), "not a pipe", CancellationToken.NONE);
            check(false, "pipe accepted");
        } catch (IllegalArgumentException expected) {
            check(true, "pipe check");
        }

        try (NexusClient<InteropServerProxy> client = InteropScaffolding.INSTANCE.createClient(ClientConfig.tcp("localhost", 1).build(), new Host())) {
            check(client.proxy() != null, "client proxy");
        }

        check(TypedJson.base64(TypedJson.fromBase64("AAECAwQ=")).equals("AAECAwQ="), "base64");
        check(TypedJson.base64(new byte[0]).isEmpty() && TypedJson.fromBase64("").length == 0, "base64 empty");
        check(Arrays.equals(TypedJson.fromBase64("/w=="), new byte[] {(byte) 0xFF}), "base64 padding");
    }

    /**
     * Runs the checks.
     *
     * @param args unused
     */
    public static void main(String[] args) {
        for (ScaffoldingExport.Method method : InteropScaffolding.INSTANCE.methods()) {
            if (method.name().startsWith("Echo") && method.parameters().size() == 1 && method.result() == method.parameters().get(0)) {
                ECHO.put(method.result().expression(), method);
            }
        }

        scriptableNexus();
        exports();
$VECTORS
        System.out.println("vectors=" + vectorCount + " echoes=" + echoCount);
        System.out.println("checked=" + checkedCount + " failed=" + failedCount);
        System.exit(failedCount == 0 ? 0 : 1);
    }
$CHUNKS
}
""";

    private sealed class ScaffoldTempDir : IDisposable
    {
        public ScaffoldTempDir()
        {
            Root = Path.Combine(Path.GetTempPath(), "nexnet-k11-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Source);
            Directory.CreateDirectory(Classes);
        }

        public string Root { get; }

        public string Source => Path.Combine(Root, "src");

        public string Classes => Path.Combine(Root, "classes");

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
