using System.Globalization;
using System.Text;
using System.Text.Json;
using NexNet.Generator.Ir;
using NexNet.Generator.Targets.JavaScript;

namespace NexNet.Generator.Tests.Targets.JavaScript;

/// <summary>
/// SDK-gated checks of the JavaScript output (D27): <c>tsc --noEmit</c> on the declarations (with a TypeScript file
/// that uses them) against the runtime package, and Node runners that replay C#-written bytes (the golden vectors and
/// the C# samples of the feature fixture) through the generated formatters. Skipped without
/// <c>NEXNET_REQUIRE_SDKS=1</c> or <c>NEXNET_TEST_LANGS=node</c>.
/// </summary>
public class JsCompileTests
{
    private const string Rt = "rt.";

    private static readonly SemaphoreSlim NodeLock = new(4, 4);

    // ------------------------------------------------------------------ C# samples (no SDK)

    [Test]
    public async Task FeatureSamplesSerializeInCSharp()
    {
        var samples = FeatureSamples();
        await Assert.That(samples.Count).IsGreaterThan(15);
        await Assert.That(samples.All(s => s.Hex.Length > 0)).IsTrue();
    }

    [Test]
    public async Task GoldenRunnerCoversTheZoo()
    {
        GoldenRunner(JsEmitterTests.Zoo, out var count);
        await Assert.That(count).IsGreaterThan(60);
    }

    // ------------------------------------------------------------------ tsc

    [Test]
    public async Task ZooDeclarationsTypeCheck()
    {
        var sdk = await NodeSdk.Get();
        using var workspace = await Workspace.Create(JsEmitterTests.Zoo, ZooSupport());
        await TypeCheck(sdk, workspace, JsEmitterTests.Zoo, ZooUsage);
    }

    [Test]
    public async Task FeatureDeclarationsTypeCheck()
    {
        var sdk = await NodeSdk.Get();
        using var workspace = await Workspace.Create(JsEmitterTests.Features, FeatureSupport());
        await TypeCheck(sdk, workspace, JsEmitterTests.Features, FeatureUsage);
    }

    // ------------------------------------------------------------------ runners

    [Test]
    public async Task ZooReplaysGoldenVectors()
    {
        var sdk = await NodeSdk.Get();
        using var workspace = await Workspace.Create(JsEmitterTests.Zoo, ZooSupport());
        var output = await RunNode(sdk, workspace, GoldenRunner(JsEmitterTests.Zoo, out var count));
        await Assert.That(output).Contains("checked=" + count.ToString(CultureInfo.InvariantCulture) + " failed=0");
    }

    [Test]
    public async Task FeaturesMatchCSharpBytes()
    {
        var sdk = await NodeSdk.Get();
        using var workspace = await Workspace.Create(JsEmitterTests.Features, FeatureSupport());
        var runner = FeatureRunner();
        var count = runner.Split('\n').Count(l => l.StartsWith("roundTrip(", StringComparison.Ordinal) || l.StartsWith("encodes(", StringComparison.Ordinal));
        var output = await RunNode(sdk, workspace, runner);
        await Assert.That(output).Contains("checked=" + count.ToString(CultureInfo.InvariantCulture) + " failed=0");
    }

    // ------------------------------------------------------------------ plumbing

    private static async Task TypeCheck(NodeSdk sdk, Workspace workspace, JsRun run, string extraUsage)
    {
        var usage = Usage(run) + extraUsage;
        await workspace.Write("usage.ts", usage);
        var files = run.Files.Keys.Where(f => f.EndsWith(".d.ts", StringComparison.Ordinal))
            .Concat(workspace.SupportFiles.Where(f => f.EndsWith(".d.ts", StringComparison.Ordinal)))
            .Append("usage.ts")
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToArray();
        var runtime = Path.Combine(NodeSdk.RuntimePackage, "src", "index.js").Replace('\\', '/');
        var config = new
        {
            compilerOptions = new Dictionary<string, object>
            {
                ["target"] = "ES2022",
                ["module"] = "NodeNext",
                ["moduleResolution"] = "NodeNext",
                ["lib"] = new[] { "ES2023", "DOM", "DOM.Iterable", "ESNext.Disposable" },
                ["types"] = Array.Empty<string>(),
                ["strict"] = true,
                ["noEmit"] = true,
                ["allowJs"] = true,
                ["checkJs"] = false,
                ["skipLibCheck"] = false,
                ["paths"] = new Dictionary<string, string[]> { ["@dtronix/nexnet"] = new[] { runtime } },
            },
            files,
        };
        await workspace.Write("tsconfig.json", JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));

        NodeProcessResult result;
        await NodeLock.WaitAsync();
        try
        {
            result = await sdk.TypeCheck(Path.Combine(workspace.Root, "tsconfig.json"));
        }
        finally
        {
            NodeLock.Release();
        }

        await Assert.That(result.Success).IsTrue().Because("tsc failed:\n" + result.Output);
        await Assert.That(result.Output.Trim()).IsEmpty().Because("tsc printed:\n" + result.Output);
    }

    private static async Task<string> RunNode(NodeSdk sdk, Workspace workspace, string runner)
    {
        await workspace.Write("runner.js", runner);
        NodeProcessResult result;
        await NodeLock.WaitAsync();
        try
        {
            result = await sdk.RunScript(Path.Combine(workspace.Root, "runner.js"));
        }
        finally
        {
            NodeLock.Release();
        }

        await Assert.That(result.Success).IsTrue().Because("The runner failed:\n" + result.Output);
        return result.Output;
    }

    /// <summary>A TypeScript file that constructs every concrete class and writes it with its formatter.</summary>
    private static string Usage(JsRun run)
    {
        var sb = new StringBuilder();
        sb.Append("import { MsgPackWriter } from '@dtronix/nexnet';\n");
        sb.Append("import * as F from './formatters.js';\n");
        var objects = run.Ir!.Types.OfType<ObjectDeclIr>().Where(o => !o.IsAbstract).ToArray();
        foreach (var obj in objects)
        {
            var name = JavaScriptTarget.Instance.TypeName(obj);
            sb.Append("import { ").Append(name).Append(" } from './").Append(name).Append(".js';\n");
        }

        sb.Append("const w = new MsgPackWriter();\n");
        foreach (var obj in objects)
        {
            var name = JavaScriptTarget.Instance.TypeName(obj);
            var local = "v_" + name;
            if (obj.TypeParams.Length == 0)
            {
                sb.Append("const ").Append(local).Append(": ").Append(name).Append(" = new ").Append(name).Append("();\n");
                sb.Append("F.").Append(JsFormatterExpressions.FormatterName(name)).Append(".write(w, ").Append(local).Append(");\n");
            }
            else
            {
                var args = string.Join(", ", obj.TypeParams.Select(_ => "unknown"));
                sb.Append("const ").Append(local).Append(" = new ").Append(name).Append('<').Append(args).Append(">();\n");
            }

            sb.Append("export const eq_").Append(name).Append(": boolean = ").Append(local).Append(".equals(").Append(local)
                .Append(") && ").Append(local).Append(".hashKey().length > 0;\n");
        }

        return sb.ToString();
    }

    private const string ZooUsage = """
        import { int32Formatter, type Formatter } from '@dtronix/nexnet';
        import { InteropShape } from './InteropShape.js';
        import { IInteropMessage } from './IInteropMessage.js';
        import { InteropEnum } from './InteropEnum.js';
        import { InteropLongEnum } from './InteropLongEnum.js';
        import { InteropFlags } from './InteropFlags.js';
        import { InteropPointFormatter } from './interop-point-formatter.js';
        import type { InteropPointFormatter as InteropPointFormatterContract } from './user-formatters.js';

        export const item: InteropItem = new InteropItem({ id: 1, name: null });
        export const itemFormatter: Formatter<InteropItem | null> = F.InteropItemFormatter;
        export const shape: InteropShape = new InteropCircle({ radius: 2 });
        export const shapeCase: InteropShape.Case = new InteropRectangle({ width: 1, height: 2 });
        export const message: IInteropMessage = new InteropTextMessage({ text: 'x' });
        export const isMessage: boolean = shape instanceof IInteropMessage;
        export const enumValue: InteropEnum = InteropEnum.Two;
        export const longEnumValue: InteropLongEnum = InteropLongEnum.BeyondDouble;
        export const flags: InteropFlags = InteropFlags.A | InteropFlags.High;
        export const box: Formatter<InteropBox<number> | null> = F.InteropBoxFormatter(int32Formatter);
        export const pair: InteropPair<string, number> = new InteropPair<string, number>({ first: 'a', second: [1, 2] });
        export const structValue: InteropStruct = new InteropStruct({ x: 1, y: 2, label: null });
        export const structFormatter: Formatter<InteropStruct> = F.InteropStructFormatter;
        export const point: InteropPointFormatterContract = InteropPointFormatter;
        // @ts-expect-error a plain object is not a union case
        export const notShape: InteropShape = {};
        // @ts-expect-error not a value of the enum
        export const notEnum: InteropEnum = 3;
        """;

    private const string FeatureUsage = """
        import { MoneyFormatter } from './ext/money-formatter.js';
        import type { MoneyFormatter as MoneyFormatterContract } from './user-formatters.js';
        import { Shape } from './Shape.js';
        import { IEvent } from './IEvent.js';
        import { Small } from './Small.js';
        import { Huge } from './Huge.js';

        export const money: MoneyFormatterContract = MoneyFormatter;
        export const shape: Shape = new BigCircle({ radius: 1, label: 'x' });
        export const circle: Circle = shape instanceof Circle ? shape : new Circle();
        export const event: IEvent = new Spot({ x: 1, cost: 5n });
        export const flags: Small = Small.A | Small.Top;
        export const huge: Huge = Huge.Max;
        export const tree: Tree<string> = new Tree<string>({ value: 'root', children: [new Tree<string>({ value: 'leaf' })] });
        """;

    private sealed class Workspace : IDisposable
    {
        private Workspace(string root, IReadOnlyList<string> supportFiles)
        {
            Root = root;
            SupportFiles = supportFiles;
        }

        public string Root { get; }

        public IReadOnlyList<string> SupportFiles { get; }

        /// <summary>A temporary directory with the emitted files, the support files and the runtime package linked in.</summary>
        public static async Task<Workspace> Create(JsRun run, Dictionary<string, string> support)
        {
            await Assert.That(run.Diagnostics.Select(d => d.ToString())).IsEmpty();
            var root = Path.Combine(Path.GetTempPath(), "nexnet-j03-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var workspace = new Workspace(root, support.Keys.ToArray());
            foreach (var (path, content) in run.Files.Concat(support))
                await workspace.Write(path, content);
            await workspace.Write("package.json", "{ \"private\": true, \"type\": \"module\" }\n");
            await NodeSdk.LinkRuntime(root);
            return workspace;
        }

        public async Task Write(string path, string content)
        {
            var full = Path.Combine(Root, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            await File.WriteAllTextAsync(full, content);
        }

        public void Dispose()
        {
            NodeSdk.UnlinkRuntime(Root);
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

    private static Dictionary<string, string> ZooSupport() => new()
    {
        ["interop-point-formatter.js"] = """
            /** Test formatter of the C# InteropPoint: [X, Y], as a [x, y] array. */
            export const InteropPointFormatter = Object.freeze({
              /** @param {import('@dtronix/nexnet').MsgPackWriter} w @param {[number, number]} v */
              write(w, v) {
                w.writeArrayHeader(2);
                w.writeInt(v[0]);
                w.writeInt(v[1]);
              },
              /** @param {import('@dtronix/nexnet').MsgPackReader} r @returns {[number, number]} */
              read(r) {
                r.readArrayHeader(2);
                const x = r.readInt32();
                return [x, r.readInt32()];
              },
            });
            """,
        ["interop-point-formatter.d.ts"] = """
            import type { Formatter } from '@dtronix/nexnet';
            export declare const InteropPointFormatter: Formatter<[number, number]>;
            """,
    };

    private static Dictionary<string, string> FeatureSupport() => new()
    {
        ["ext/money-formatter.js"] = """
            /** Test formatter of the C# Money struct: its cents; null (an unset member) writes the C# default. */
            export const MoneyFormatter = Object.freeze({
              /** @param {import('@dtronix/nexnet').MsgPackWriter} w @param {bigint | null} v */
              write(w, v) {
                w.writeInt64(v === null || v === undefined ? 0n : v);
              },
              /** @param {import('@dtronix/nexnet').MsgPackReader} r @returns {bigint} */
              read(r) {
                return r.readInt64();
              },
            });
            """,
        ["ext/money-formatter.d.ts"] = """
            import type { Formatter } from '@dtronix/nexnet';
            export declare const MoneyFormatter: Formatter<bigint | null>;
            """,
    };

    private const string RunnerBase = """
        import * as rt from '@dtronix/nexnet';
        import * as F from './formatters.js';
        import { equalsValue } from './nexnet-support.js';

        let checkedCount = 0;
        let failedCount = 0;

        /** @param {string} hex */
        function bytes(hex) {
          const result = new Uint8Array(hex.length / 2);
          for (let i = 0; i < result.length; i++) result[i] = parseInt(hex.substring(2 * i, 2 * i + 2), 16);
          return result;
        }

        /** @param {Uint8Array} data */
        function hex(data) {
          return Array.from(data, (b) => b.toString(16).padStart(2, '0')).join('');
        }

        function fail(id, message) {
          failedCount++;
          console.log('FAIL ' + id + ': ' + message);
        }

        function decode(f, data, maxDepth) {
          const r = new rt.MsgPackReader(data, { preset: 'untrusted', maxDepth });
          const value = f.read(r);
          r.ensureEnd();
          return value;
        }

        function encode(f, value) {
          const w = new rt.MsgPackWriter();
          f.write(w, value);
          return w.toUint8Array().slice();
        }

        /** Decodes, re-encodes (must give canonical when set) and decodes again (must be equal, with the same hash key). */
        function roundTrip(id, h, canonical, maxDepth, f) {
          checkedCount++;
          try {
            const value = decode(f, bytes(h), maxDepth);
            const again = encode(f, value);
            if (canonical !== null && hex(again) !== canonical) {
              fail(id, 're-encoded as ' + hex(again) + ', expected ' + canonical);
              return;
            }
            const second = decode(f, again, maxDepth);
            if (!equalsValue(value, second)) {
              fail(id, 'decoded values differ');
            } else if (value !== null && typeof value === 'object' && typeof value.hashKey === 'function' && value.hashKey() !== second.hashKey()) {
              fail(id, 'hash keys differ');
            }
          } catch (e) {
            fail(id, String(e && e.stack ? e.stack : e));
          }
        }

        /** Decoding must fail with a NexusSerializationError. */
        function reject(id, h, maxDepth, f) {
          checkedCount++;
          try {
            const value = decode(f, bytes(h), maxDepth);
            fail(id, 'decoded ' + String(value));
          } catch (e) {
            if (!(e instanceof rt.NexusSerializationError)) fail(id, 'threw ' + String(e));
          }
        }

        /** A JS-built value must encode as C# encodes the same value. */
        function encodes(id, h, f, value) {
          checkedCount++;
          try {
            const actual = hex(encode(f, value));
            if (actual !== h) fail(id, 'encoded as ' + actual + ', C# wrote ' + h);
          } catch (e) {
            fail(id, String(e && e.stack ? e.stack : e));
          }
        }

        """;

    private static string Runner(IEnumerable<string> imports, IEnumerable<string> calls)
    {
        var sb = new StringBuilder();
        foreach (var import in imports)
            sb.Append(import).Append('\n');
        sb.Append(RunnerBase);
        foreach (var call in calls)
            sb.Append(call).Append('\n');
        sb.Append("console.log('checked=' + checkedCount + ' failed=' + failedCount);\n");
        sb.Append("process.exitCode = failedCount === 0 ? 0 : 1;\n");
        return sb.ToString();
    }

    private static string Str(string value) => JsSyntax.String(value);

    // ------------------------------------------------------------------ golden vectors

    private static string GoldenRunner(JsRun zoo, out int count)
    {
        var decls = zoo.Ir!.Types.ToDictionary(d => d.Name, StringComparer.Ordinal);
        var golden = Path.Combine(JsTestRunner.RepoRoot(), "tests", "golden");
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

                var expression = GoldenType.Translate(type, decls);
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
        return Runner(new[] { "import { InteropPointFormatter } from './interop-point-formatter.js';" }, calls);
    }

    /// <summary>Golden type expressions (tests/golden/README.md) as JavaScript formatter expressions.</summary>
    private static class GoldenType
    {
        private static readonly Dictionary<string, PrimKind> Prims = new(StringComparer.Ordinal)
        {
            ["bool"] = PrimKind.Bool, ["sbyte"] = PrimKind.SByte, ["byte"] = PrimKind.Byte, ["short"] = PrimKind.Int16,
            ["ushort"] = PrimKind.UInt16, ["int"] = PrimKind.Int32, ["uint"] = PrimKind.UInt32, ["long"] = PrimKind.Int64,
            ["ulong"] = PrimKind.UInt64, ["float"] = PrimKind.Single, ["double"] = PrimKind.Double, ["half"] = PrimKind.Half,
            ["char"] = PrimKind.Char, ["decimal"] = PrimKind.Decimal, ["string"] = PrimKind.String, ["DateTime"] = PrimKind.DateTime,
            ["DateTimeOffset"] = PrimKind.DateTimeOffset, ["TimeSpan"] = PrimKind.TimeSpan, ["DateOnly"] = PrimKind.DateOnly,
            ["TimeOnly"] = PrimKind.TimeOnly, ["Guid"] = PrimKind.Guid, ["BigInteger"] = PrimKind.BigInteger, ["Uri"] = PrimKind.Uri,
            ["Version"] = PrimKind.Version,
        };

        private static readonly HashSet<string> Ext78 = new(StringComparer.Ordinal)
        {
            "sbyte", "short", "ushort", "int", "uint", "long", "ulong", "float", "double", "char", "half",
        };

        private static readonly HashSet<string> ReferencePrims = new(StringComparer.Ordinal) { "string", "Uri", "Version" };

        private sealed record Expr(string Name, List<Expr> Args, List<string> Suffixes);

        public static string? Translate(string text, Dictionary<string, TypeDeclIr> decls)
        {
            var position = 0;
            var expr = Parse(text, ref position);
            return expr == null || position != text.Length ? null : Js(expr, expr.Suffixes.Count, decls);
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

        private static string? Js(Expr expr, int suffixes, Dictionary<string, TypeDeclIr> decls)
        {
            if (suffixes > 0)
            {
                var suffix = expr.Suffixes[suffixes - 1];
                if (suffix == "?")
                {
                    var inner = Js(expr, suffixes - 1, decls);
                    return inner == null ? null : IsValueType(expr, suffixes - 1, decls) ? Rt + "nullableFormatter(" + inner + ")" : inner;
                }

                if (suffixes == 1 && expr.Args.Count == 0 && expr.Name == "byte")
                    return Rt + "binFormatter";
                if (suffixes == 1 && expr.Args.Count == 0 && Ext78.Contains(expr.Name))
                    return Rt + JsFormatterExpressions.PrimArrayFormatter(Prims[expr.Name]);
                var element = Js(expr, suffixes - 1, decls);
                return element == null ? null : Rt + "arrayFormatter(" + element + ")";
            }

            var args = new List<string>();
            foreach (var arg in expr.Args)
            {
                var a = Js(arg, arg.Suffixes.Count, decls);
                if (a == null)
                    return null;
                args.Add(a);
            }

            var e = expr.Args.Count == 1 ? expr.Args[0] : null;
            switch (expr.Name)
            {
                case "List" when args.Count == 1:
                    return e!.Suffixes.Count == 0 && e.Args.Count == 0 && Ext78.Contains(e.Name)
                        ? Rt + JsFormatterExpressions.PrimArrayFormatter(Prims[e.Name])
                        : Rt + "arrayFormatter(" + args[0] + ")";
                case "Memory" or "ReadOnlyMemory" when args.Count == 1:
                    if (e!.Suffixes.Count == 0 && e.Args.Count == 0 && e.Name == "byte")
                        return Rt + "binFormatter";
                    return e.Suffixes.Count == 0 && e.Args.Count == 0 && Ext78.Contains(e.Name)
                        ? Rt + JsFormatterExpressions.PrimArrayFormatter(Prims[e.Name])
                        : Rt + "arrayFormatter(" + args[0] + ")";
                case "IList" or "IReadOnlyList" or "IEnumerable" or "ICollection" or "IReadOnlyCollection" or "Queue" or "Stack" when args.Count == 1:
                    return Rt + "arrayFormatter(" + args[0] + ")";
                case "HashSet" when args.Count == 1:
                    return Rt + "setFormatter(" + args[0] + ")";
                case "Dictionary" or "IDictionary" or "IReadOnlyDictionary" when args.Count == 2:
                    return Rt + "mapFormatter(" + args[0] + ", " + args[1] + ")";
                case "KeyValuePair" when args.Count == 2:
                    return Rt + "keyValuePairFormatter(" + args[0] + ", " + args[1] + ")";
                case "InteropPoint" when args.Count == 0:
                    return "InteropPointFormatter";
            }

            if (args.Count == 0 && Prims.TryGetValue(expr.Name, out var prim))
                return Rt + JsFormatterExpressions.PrimFormatter(prim);

            if (decls.ContainsKey(expr.Name))
                return "F." + JsFormatterExpressions.FormatterName(expr.Name) + (args.Count == 0 ? "" : "(" + string.Join(", ", args) + ")");

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

        var assembly = JsEmitterTests.Features.Load();
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
        string? Formatter(string name) => name switch
        {
            _ when name.StartsWith("Defaults.", StringComparison.Ordinal) => "F.DefaultsFormatter",
            _ when name.StartsWith("Everything.", StringComparison.Ordinal) => "F.EverythingFormatter",
            "Empty.new" => "F.EmptyFormatter",
            "Holder<int>" => "F.HolderFormatter(rt.int32Formatter)",
            "Holder<byte>" => "F.HolderFormatter(rt.byteFormatter)",
            "Holder<string>" => "F.HolderFormatter(rt.stringFormatter)",
            "Holder<ushort>" => "F.HolderFormatter(rt.uint16Formatter)",
            "Holder<Half>" => "F.HolderFormatter(rt.halfFormatter)",
            "Tree<string>" => "F.TreeFormatter(rt.stringFormatter)",
            "Ping<int>" => "F.PingFormatter(rt.int32Formatter)",
            _ when name.StartsWith("Shape.", StringComparison.Ordinal) => "F.ShapeFormatter",
            _ when name.StartsWith("IEvent.", StringComparison.Ordinal) => "F.IEventFormatter",
            "Inner" => "F.Outer_InnerFormatter",
            _ => null,
        };

        var imports = new List<string>();
        var calls = new List<string>();
        foreach (var sample in FeatureSamples())
        {
            var formatter = Formatter(sample.Name) ?? throw new InvalidOperationException("No JavaScript formatter for the sample " + sample.Name);
            calls.Add("roundTrip(" + Str(sample.Name) + ", " + Str(sample.Hex) + ", " + Str(sample.Hex) + ", 64, " + formatter + ");");
            if (sample.Name.EndsWith(".new", StringComparison.Ordinal))
            {
                var type = sample.Name.Substring(0, sample.Name.Length - 4);
                imports.Add("import { " + type + " } from './" + type + ".js';");
                calls.Add("encodes(" + Str(sample.Name + ".js") + ", " + Str(sample.Hex) + ", " + formatter + ", new " + type + "());");
            }
        }

        return Runner(imports, calls);
    }
}
