using System.Globalization;
using System.Text;
using System.Text.Json;
using NexNet.Generator.Ir;
using NexNet.Generator.Targets.Java;

namespace NexNet.Generator.Tests.Targets.Java;

/// <summary>
/// SDK-gated checks of the Java output (D27): <c>javac --release 11 -Xlint:all -Werror</c> against the runtime jar, and
/// runners that replay C#-written bytes (the golden vectors and C# samples of the feature fixture) through the
/// generated formatters. Skipped without <c>NEXNET_REQUIRE_SDKS=1</c> or <c>NEXNET_TEST_LANGS=java</c>.
/// </summary>
public class JavaCompileTests
{
    private const string Rt = "com.dtronix.nexnet.serialization.Formatters";

    private static readonly SemaphoreSlim JavaLock = new(4, 4);

    // ------------------------------------------------------------------ C# samples (no SDK)

    [Test]
    public async Task FeatureSamplesSerializeInCSharp()
    {
        var samples = FeatureSamples();
        await Assert.That(samples.Count).IsGreaterThan(15);
        await Assert.That(samples.All(s => s.Hex.Length > 0)).IsTrue();
    }

    // ------------------------------------------------------------------ javac

    [Test]
    public async Task ZooCompilesWithJavac()
    {
        var sdk = await JavaSdk.Get();
        using var dir = new TempDir();
        await Compile(sdk, dir, JavaEmitterTests.Zoo, ZooSupport());
    }

    [Test]
    public async Task FeaturesCompileWithJavac()
    {
        var sdk = await JavaSdk.Get();
        using var dir = new TempDir();
        await Compile(sdk, dir, JavaEmitterTests.Features, FeatureSupport());
    }

    // ------------------------------------------------------------------ runners

    [Test]
    public async Task ZooReplaysGoldenVectors()
    {
        var sdk = await JavaSdk.Get();
        using var dir = new TempDir();
        var runner = GoldenRunner(JavaEmitterTests.Zoo, out var count);
        await Assert.That(count).IsGreaterThan(60);
        var output = await CompileAndRun(sdk, dir, JavaEmitterTests.Zoo, ZooSupport(), runner);
        await Assert.That(output).Contains("failed=0");
    }

    [Test]
    public async Task FeaturesMatchCSharpBytes()
    {
        var sdk = await JavaSdk.Get();
        using var dir = new TempDir();
        var output = await CompileAndRun(sdk, dir, JavaEmitterTests.Features, FeatureSupport(), FeatureRunner());
        await Assert.That(output).Contains("failed=0");
    }

    // ------------------------------------------------------------------ plumbing

    private static async Task Compile(JavaSdk sdk, TempDir dir, JavaRun run, Dictionary<string, string> support)
    {
        await Assert.That(run.Diagnostics.Select(d => d.ToString())).IsEmpty();
        var sources = new List<string>();
        foreach (var (path, content) in run.Files.Concat(support))
        {
            var full = Path.Combine(dir.Source, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            await File.WriteAllTextAsync(full, content);
            sources.Add(path);
        }

        ProcessResult result;
        await JavaLock.WaitAsync();
        try
        {
            result = await sdk.Compile(dir.Source, sources, dir.Classes);
        }
        finally
        {
            JavaLock.Release();
        }

        await Assert.That(result.Success).IsTrue().Because("javac failed:\n" + result.Output);
        await Assert.That(result.Output.Trim()).IsEmpty().Because("javac printed:\n" + result.Output);
    }

    private static async Task<string> CompileAndRun(JavaSdk sdk, TempDir dir, JavaRun run, Dictionary<string, string> support, string runner)
    {
        support = new Dictionary<string, string>(support) { ["k03/runner/Runner.java"] = runner };
        await Compile(sdk, dir, run, support);
        var result = await sdk.RunMain(dir.Classes, "k03.runner.Runner");
        await Assert.That(result.Success).IsTrue().Because("The runner failed:\n" + result.Output);
        return result.Output;
    }

    private static Dictionary<string, string> ZooSupport() => new()
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

    private static Dictionary<string, string> FeatureSupport() => new()
    {
        ["com/acme/ext/MoneyFormatter.java"] = """
            package com.acme.ext;

            import com.dtronix.nexnet.serialization.Formatter;
            import com.dtronix.nexnet.serialization.MsgPackReader;
            import com.dtronix.nexnet.serialization.MsgPackWriter;

            /** Test formatter of the C# Money struct: its cents; null (an unset member) writes the C# default. */
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

    private const string RunnerBase = """
        package k03.runner;

        import com.dtronix.nexnet.serialization.Formatter;
        import com.dtronix.nexnet.serialization.MsgPackException;
        import com.dtronix.nexnet.serialization.MsgPackOptions;

        /** Replays C#-written bytes through the generated formatters. */
        public final class Runner {
            private static int checkedCount;
            private static int failedCount;

            private Runner() {
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

            private static void fail(String id, String message) {
                failedCount++;
                System.out.println("FAIL " + id + ": " + message);
            }

            private static <T> T decode(Formatter<T> f, byte[] data, int maxDepth) {
                return com.dtronix.nexnet.serialization.Formatters.deserialize(f, data, MsgPackOptions.UNTRUSTED.withMaxDepth(maxDepth));
            }

            /** Decodes, re-encodes (must give {@code canonical} when set) and decodes again (must be equal). */
            static <T> void roundTrip(String id, String hex, String canonical, int maxDepth, Formatter<T> f) {
                checkedCount++;
                try {
                    T value = decode(f, bytes(hex), maxDepth);
                    byte[] again = com.dtronix.nexnet.serialization.Formatters.serialize(f, value);
                    if (canonical != null && !canonical.equals(hex(again))) {
                        fail(id, "re-encoded as " + hex(again) + ", expected " + canonical);
                        return;
                    }
                    T second = decode(f, again, maxDepth);
                    if (!java.util.Objects.deepEquals(value, second)) {
                        fail(id, "decoded values differ: " + value + " / " + second);
                    } else if (value != null && value.hashCode() != second.hashCode()) {
                        fail(id, "hash codes differ: " + value);
                    }
                } catch (RuntimeException e) {
                    fail(id, e.toString());
                }
            }

            /** Decoding must fail with a MsgPackException. */
            static <T> void reject(String id, String hex, int maxDepth, Formatter<T> f) {
                checkedCount++;
                try {
                    T value = decode(f, bytes(hex), maxDepth);
                    fail(id, "decoded " + value);
                } catch (MsgPackException expected) {
                    // rejected
                } catch (RuntimeException e) {
                    fail(id, "threw " + e);
                }
            }

            /** A Java-built value must encode as C# encodes the same value. */
            static <T> void encodes(String id, String hex, Formatter<T> f, T value) {
                checkedCount++;
                try {
                    String actual = hex(com.dtronix.nexnet.serialization.Formatters.serialize(f, value));
                    if (!hex.equals(actual)) {
                        fail(id, "encoded as " + actual + ", C# wrote " + hex);
                    }
                } catch (RuntimeException e) {
                    fail(id, e.toString());
                }
            }

            /**
             * Runs the checks.
             *
             * @param args unused
             */
            public static void main(String[] args) {
                run();
                System.out.println("checked=" + checkedCount + " failed=" + failedCount);
                System.exit(failedCount == 0 ? 0 : 1);
            }

            private static void run() {
        """;

    private static string Runner(IEnumerable<string> calls)
    {
        var sb = new StringBuilder(RunnerBase);
        foreach (var call in calls)
            sb.Append("        ").Append(call).Append('\n');
        sb.Append("    }\n}\n");
        return sb.ToString();
    }

    // ------------------------------------------------------------------ golden vectors

    private static string GoldenRunner(JavaRun zoo, out int count)
    {
        var decls = zoo.Ir!.Types.ToDictionary(d => d.Name, StringComparer.Ordinal);
        var formatters = JavaFixtures.ZooPackage + ".formatters.Formatters";
        var golden = Path.Combine(JavaTestRunner.RepoRoot(), "tests", "golden");
        var calls = new List<string>();
        foreach (var name in new[] { "objects.jsonl", "unions.jsonl", "containers.jsonl", "reject.jsonl" })
        {
            foreach (var line in File.ReadAllLines(Path.Combine(golden, name)))
            {
                if (line.Trim().Length == 0)
                    continue;
                using var json = JsonDocument.Parse(line);
                var root = json.RootElement;
                var type = root.GetProperty("type").GetString()!;
                if (!type.Contains("Interop", StringComparison.Ordinal))
                    continue;

                var expression = GoldenType.Translate(type, decls, formatters);
                if (expression == null)
                    continue;

                var maxDepth = 64;
                if (root.TryGetProperty("options", out var options))
                {
                    if (options.EnumerateObject().Any(p => p.Name != "maxDepth"))
                        continue;
                    maxDepth = options.GetProperty("maxDepth").GetInt32();
                }

                var id = Str(root.GetProperty("id").GetString()!);
                var hex = Str(root.GetProperty("hex").GetString()!);
                var depth = maxDepth.ToString(CultureInfo.InvariantCulture);
                calls.Add(root.GetProperty("mode").GetString() switch
                {
                    "both" => "roundTrip(" + id + ", " + hex + ", " + hex + ", " + depth + ", " + expression + ");",
                    "decode" => "roundTrip(" + id + ", " + hex + ", null, " + depth + ", " + expression + ");",
                    _ => "reject(" + id + ", " + hex + ", " + depth + ", " + expression + ");",
                });
            }
        }

        count = calls.Count;
        return Runner(calls);
    }

    private static string Str(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    /// <summary>Golden type expressions (tests/golden/README.md) as Java formatter expressions.</summary>
    private static class GoldenType
    {
        private static readonly Dictionary<string, string> Prims = new(StringComparer.Ordinal)
        {
            ["bool"] = "BOOLEAN", ["sbyte"] = "SBYTE", ["byte"] = "BYTE", ["short"] = "SHORT", ["ushort"] = "USHORT",
            ["int"] = "INT", ["uint"] = "UINT", ["long"] = "LONG", ["ulong"] = "ULONG", ["float"] = "FLOAT",
            ["double"] = "DOUBLE", ["half"] = "HALF", ["char"] = "CHAR", ["decimal"] = "DECIMAL", ["string"] = "STRING",
            ["DateTime"] = "DATE_TIME", ["DateTimeOffset"] = "DATE_TIME_OFFSET", ["TimeSpan"] = "TIME_SPAN",
            ["DateOnly"] = "DATE_ONLY", ["TimeOnly"] = "TIME_ONLY", ["Guid"] = "GUID", ["BigInteger"] = "BIG_INTEGER",
            ["Uri"] = "URI", ["Version"] = "VERSION",
        };

        private static readonly HashSet<string> Ext78 = new(StringComparer.Ordinal)
        {
            "sbyte", "short", "ushort", "int", "uint", "long", "ulong", "float", "double", "char", "half",
        };

        private static readonly HashSet<string> ReferencePrims = new(StringComparer.Ordinal) { "string", "Uri", "Version" };

        private sealed record Expr(string Name, List<Expr> Args, List<string> Suffixes);

        public static string? Translate(string text, Dictionary<string, TypeDeclIr> decls, string formatters)
        {
            var position = 0;
            var expr = Parse(text, ref position);
            return expr == null || position != text.Length ? null : Java(expr, expr.Suffixes.Count, decls, formatters);
        }

        private static Expr? Parse(string text, ref int position)
        {
            var start = position;
            while (position < text.Length && (char.IsLetterOrDigit(text[position]) || text[position] == '_'))
                position++;
            if (position == start)
                return null;

            var expr = new Expr(text.Substring(start, position - start), new List<Expr>(), new List<string>());
            if (position < text.Length && text[position] == '<')
            {
                do
                {
                    position++;
                    var arg = Parse(text, ref position);
                    if (arg == null)
                        return null;
                    expr.Args.Add(arg);
                }
                while (position < text.Length && text[position] == ',');

                if (position >= text.Length || text[position] != '>')
                    return null;
                position++;
            }

            while (position < text.Length)
            {
                if (text[position] == '?')
                {
                    expr.Suffixes.Add("?");
                    position++;
                }
                else if (text[position] == '[' && position + 1 < text.Length && text[position + 1] == ']')
                {
                    expr.Suffixes.Add("[]");
                    position += 2;
                }
                else
                {
                    break;
                }
            }

            return expr;
        }

        private static bool IsValueType(Expr expr, int suffixes, Dictionary<string, TypeDeclIr> decls)
            => suffixes == 0 && expr.Args.Count == 0
               && ((Prims.ContainsKey(expr.Name) && !ReferencePrims.Contains(expr.Name))
                   || (decls.TryGetValue(expr.Name, out var d) && d is EnumDeclIr or ObjectDeclIr { IsStruct: true }));

        private static string? Java(Expr expr, int suffixes, Dictionary<string, TypeDeclIr> decls, string formatters)
        {
            if (suffixes > 0)
            {
                var suffix = expr.Suffixes[suffixes - 1];
                if (suffix == "?")
                {
                    var inner = Java(expr, suffixes - 1, decls, formatters);
                    return inner == null ? null : IsValueType(expr, suffixes - 1, decls) ? Rt + ".nullable(" + inner + ")" : inner;
                }

                if (suffixes == 1 && expr.Args.Count == 0 && expr.Name == "byte")
                    return Rt + ".BINARY";
                if (suffixes == 1 && expr.Args.Count == 0 && Ext78.Contains(expr.Name))
                    return Rt + "." + Prims[expr.Name] + "_ARRAY";
                var element = Java(expr, suffixes - 1, decls, formatters);
                return element == null ? null : Rt + ".list(" + element + ")";
            }

            var args = new List<string>();
            foreach (var arg in expr.Args)
            {
                var a = Java(arg, arg.Suffixes.Count, decls, formatters);
                if (a == null)
                    return null;
                args.Add(a);
            }

            switch (expr.Name)
            {
                case "List" when args.Count == 1:
                    var e = expr.Args[0];
                    return e.Suffixes.Count == 0 && e.Args.Count == 0 && Ext78.Contains(e.Name)
                        ? Rt + "." + Prims[e.Name] + "_ARRAY"
                        : Rt + ".list(" + args[0] + ")";
                case "IList" or "IReadOnlyList" or "IEnumerable" or "ICollection" or "IReadOnlyCollection" when args.Count == 1:
                    return Rt + ".list(" + args[0] + ")";
                case "HashSet" when args.Count == 1:
                    return Rt + ".set(" + args[0] + ")";
                case "Queue" when args.Count == 1:
                    return Rt + ".queue(" + args[0] + ")";
                case "Stack" when args.Count == 1:
                    return Rt + ".stack(" + args[0] + ")";
                case "Dictionary" or "IDictionary" or "IReadOnlyDictionary" when args.Count == 2:
                    return Rt + ".map(" + args[0] + ", " + args[1] + ")";
                case "KeyValuePair" when args.Count == 2:
                    return Rt + ".keyValuePair(" + args[0] + ", " + args[1] + ")";
                case "InteropPoint" when args.Count == 0:
                    return JavaFixtures.ZooPackage + ".formatters.UserFormatters."
                           + FormatterExpressions.UserFormatterField(JavaFixtures.ZooPackage + ".InteropPointFormatter");
            }

            if (args.Count == 0 && Prims.TryGetValue(expr.Name, out var prim))
                return Rt + "." + prim;

            if (decls.ContainsKey(expr.Name))
                return formatters + "." + expr.Name + (args.Count == 0 ? "" : "(" + string.Join(", ", args) + ")");

            return null;
        }
    }

    // ------------------------------------------------------------------ feature samples

    internal sealed record Sample(string Name, string Hex);

    private static List<Sample>? _samples;

    /// <summary>The C# samples of the feature fixture, serialized by the C# NexNet serializer.</summary>
    internal static List<Sample> FeatureSamples()
    {
        if (_samples != null)
            return _samples;

        var assembly = JavaEmitterTests.Features.Load();
        var all = (System.Collections.IList)assembly.GetType("Feat.Samples")!.GetMethod("All")!.Invoke(null, null)!;
        var serialize = typeof(NexNet.Serialization.NexusSerializer).GetMethods()
            .Single(m => m.Name == "Serialize" && m.GetParameters().Length == 1);

        var result = new List<Sample>();
        foreach (var item in all)
        {
            var tuple = (System.Runtime.CompilerServices.ITuple)item!;
            var bytes = (byte[])serialize.MakeGenericMethod((Type)tuple[1]!).Invoke(null, new[] { tuple[2] })!;
            result.Add(new Sample((string)tuple[0]!, Convert.ToHexStringLower(bytes)));
        }

        return _samples = result;
    }

    private static string FeatureRunner()
    {
        var f = JavaFixtures.FeaturePackage + ".formatters.Formatters";
        var p = JavaFixtures.FeaturePackage + ".";
        string? Formatter(string name) => name switch
        {
            _ when name.StartsWith("Defaults.", StringComparison.Ordinal) => f + ".Defaults",
            _ when name.StartsWith("Everything.", StringComparison.Ordinal) => f + ".Everything",
            "Empty.new" => f + ".Empty",
            "Holder<int>" => f + ".Holder(" + Rt + ".INT)",
            "Holder<byte>" => f + ".Holder(" + Rt + ".BYTE)",
            "Holder<string>" => f + ".Holder(" + Rt + ".STRING)",
            "Holder<ushort>" => f + ".Holder(" + Rt + ".USHORT)",
            "Holder<Half>" => f + ".Holder(" + Rt + ".HALF)",
            "Tree<string>" => f + ".Tree(" + Rt + ".STRING)",
            "Ping<int>" => f + ".Ping(" + Rt + ".INT)",
            _ when name.StartsWith("Shape.", StringComparison.Ordinal) => f + ".Shape",
            _ when name.StartsWith("IEvent.", StringComparison.Ordinal) => f + ".IEvent",
            "Inner" => f + ".Outer_Inner",
            _ => null,
        };

        var calls = new List<string>();
        foreach (var sample in FeatureSamples())
        {
            var formatter = Formatter(sample.Name) ?? throw new InvalidOperationException("No Java formatter for the sample " + sample.Name);
            calls.Add("roundTrip(" + Str(sample.Name) + ", " + Str(sample.Hex) + ", " + Str(sample.Hex) + ", 64, " + formatter + ");");
            if (sample.Name.EndsWith(".new", StringComparison.Ordinal))
            {
                var type = sample.Name.Substring(0, sample.Name.Length - 4);
                calls.Add("encodes(" + Str(sample.Name + ".java") + ", " + Str(sample.Hex) + ", " + formatter + ", new " + p + type + "());");
            }
        }

        return Runner(calls);
    }

    private sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Root = Path.Combine(Path.GetTempPath(), "nexnet-k03-" + Guid.NewGuid().ToString("N"));
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
