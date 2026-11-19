using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using NexNet.Generator.Ir;

namespace NexNet.Generator.Tests.Targets.JavaScript;

/// <summary>
/// SDK-gated checks of the JavaScript test scaffolding (J11, D27): <c>tsc --noEmit</c> of the client and scaffolding
/// declarations with a TypeScript file that uses the scaffolding API, a Node import smoke test, and a Node runner that
/// drives the interop nexus's scaffolding against the runtime package: the golden vectors through the typed-JSON codecs
/// and echo invocations by name, and the scriptable client nexus through a fake script host. Skipped without
/// <c>NEXNET_REQUIRE_SDKS=1</c> or <c>NEXNET_TEST_LANGS=node</c>.
/// </summary>
public class JsScaffoldingCompileTests
{
    private static readonly SemaphoreSlim NodeLock = new(2, 2);

    /// <summary>Vectors whose value the JavaScript type cannot hold, with the reason.</summary>
    private static readonly Dictionary<string, string> Unrepresentable = new(StringComparer.Ordinal)
    {
        // The runtime holds Half[] as a Float32Array of numbers (J02): a NaN's sign and payload do not survive, so .NET's
        // Half.NaN (0xFE00) re-encodes as 0x7E00. A known parity loss (J25; docs/internals/js-runtime-contract.md
        // section 3.1, "Half NaN"). The runtime's golden tests cover the vector and compare any NaN with any NaN.
        ["ext78.half"] = "known parity loss (J25): Half[] is a Float32Array, so a NaN's sign and payload are lost (0xFE00 re-encodes as 0x7E00)",
    };

    [Test]
    public async Task ChatScaffoldingTypeChecks()
    {
        var sdk = await NodeSdk.Get();
        using var workspace = await ScaffoldWorkspace.Create(JsProxyEmitterTests.Chat, JsScaffoldingTests.Chat, ChatSupport);
        await workspace.Write("usage.ts", Usage);
        var files = JsProxyEmitterTests.Chat.Files.Keys.Concat(JsScaffoldingTests.Chat.Files.Keys)
            .Where(f => f.EndsWith(".d.ts", StringComparison.Ordinal))
            .Append("ext/money-formatter.d.ts")
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

        var result = await Locked(() => sdk.TypeCheck(Path.Combine(workspace.Root, "tsconfig.json")));
        await Assert.That(result.Success).IsTrue().Because("tsc failed:\n" + result.Output);
        await Assert.That(result.Output.Trim()).IsEmpty().Because("tsc printed:\n" + result.Output);
    }

    [Test]
    public async Task ChatScaffoldingImportsInNode()
    {
        var sdk = await NodeSdk.Get();
        using var workspace = await ScaffoldWorkspace.Create(JsProxyEmitterTests.Chat, JsScaffoldingTests.Chat, ChatSupport);
        await workspace.Write("smoke.js", """
            import { ChatScaffolding, ChatScriptableNexus } from './scaffolding/ChatScaffolding.js';
            import { EXPORTS, SCHEMA_HASH, find } from './scaffolding/index.js';
            import { forType } from './scaffolding/test-codecs.js';

            const item = forType('Chat.Item');
            const ok = EXPORTS.length === 1 && find('Chat.IChatClient', 'Chat.IChatServer') === ChatScaffolding
              && /^[0-9a-f]{64}$/.test(SCHEMA_HASH) && item.toJson(item.fromJson([1, 'a']))[1] === 'a'
              && ChatScaffolding.createNexus({ call: () => null }) instanceof ChatScriptableNexus;
            console.log(ok ? 'imported' : 'wrong');
            if (!ok) process.exit(1);
            """);
        var result = await Locked(() => sdk.RunScript(Path.Combine(workspace.Root, "smoke.js")));
        await Assert.That(result.Success).IsTrue().Because("Node failed:\n" + result.Output);
        await Assert.That(result.Output).Contains("imported");
    }

    [Test]
    public async Task InteropScaffoldingRunsAgainstTheRuntime()
    {
        var sdk = await NodeSdk.Get();
        await Assert.That(JsScaffoldingTests.Interop.Diagnostics.Where(d => d.Id != "NEXNET044").Select(d => d.ToString())).IsEmpty();
        using var workspace = await ScaffoldWorkspace.Create(JsScaffoldingTests.Interop, JsScaffoldingTests.InteropScaffolding, InteropSupport);
        await workspace.Write("runner.js", Runner());
        var result = await Locked(() => sdk.RunScript(Path.Combine(workspace.Root, "runner.js")));
        await Assert.That(result.Success).IsTrue().Because("The runner failed:\n" + result.Output);
        await Assert.That(result.Output).Contains("failed=0");

        var counts = Regex.Match(result.Output, @"vectors=(\d+) echoes=(\d+)");
        await Assert.That(counts.Success).IsTrue().Because(result.Output);
        await Assert.That(int.Parse(counts.Groups[1].Value, CultureInfo.InvariantCulture)).IsGreaterThan(100).Because(result.Output);
        await Assert.That(int.Parse(counts.Groups[2].Value, CultureInfo.InvariantCulture)).IsGreaterThan(50).Because(result.Output);
    }

    // ------------------------------------------------------------------ plumbing

    private static async Task<NodeProcessResult> Locked(Func<Task<NodeProcessResult>> run)
    {
        await NodeLock.WaitAsync();
        try
        {
            return await run();
        }
        finally
        {
            NodeLock.Release();
        }
    }

    /// <summary>The runner, with the IR facts it checks against filled in.</summary>
    private static string Runner()
    {
        var ir = JsScaffoldingTests.Interop.Ir!;
        var export = ir.Exports.Single(e => e.ServerApi.ClrName == "NexNet.TestNexus.Interop.IInteropServerNexus");
        var facts = new Dictionary<string, object>
        {
            ["golden"] = Path.Combine(JsTestRunner.RepoRoot(), "tests", "golden").Replace('\\', '/'),
            // Golden type expressions name declared types by simple name; the codecs use the id.
            ["ids"] = ir.Types.ToDictionary(t => t.Name, t => t.Id, StringComparer.Ordinal),
            ["clientIds"] = export.ClientApi.Methods.ToDictionary(m => m.Name, m => m.Id, StringComparer.Ordinal),
            ["exports"] = ir.Exports.Count(e => (e.Targets & (int)NexusTargets.JavaScript) != 0),
            ["schemaHash"] = NexNet.Generator.Targets.JavaScript.JsScaffoldingEmitter.SchemaHash(ir),
            ["unrepresentable"] = Unrepresentable.Keys.ToArray(),
        };
        return RunnerTemplate.Replace("\r\n", "\n").Replace("__FACTS__", JsonSerializer.Serialize(facts));
    }

    private const string Usage = """
        import { ChatScaffolding, ChatScriptableNexus } from './scaffolding/ChatScaffolding.js';
        import { EXPORTS, SCHEMA_HASH, find } from './scaffolding/index.js';
        import { C$0, C$3, SCHEMAS, forType } from './scaffolding/test-codecs.js';
        import { ON_CONNECTED, ScriptCall, TypedCodec, runScript, type ScriptHost } from './scaffolding/scaffolding-runtime.js';
        import * as tj from './scaffolding/typed-json.js';
        import type { ChatServerProxy } from './ChatServerNexus.js';
        import type { Item } from './Item.js';

        const host: ScriptHost = { call: (call: ScriptCall) => call.args[0] ?? null };
        const nexus: ChatScriptableNexus = new ChatScriptableNexus(host);
        const client = ChatScaffolding.createClient({ webSocket: { url: 'ws://localhost:1' } }, host);
        const proxy: ChatServerProxy = client.proxy;
        const result: Promise<unknown> = ChatScaffolding.invokeByName(proxy, 'Add', [1, 2], { signal: new AbortController().signal });
        const n: number = C$0.fromJson(3);
        const item: Item = C$3.fromJson([1, 'a']);
        const json: unknown = C$0.toJson(n);
        const codec: TypedCodec<any> | null = forType('int');
        const hash: string = SCHEMA_HASH;
        const found = find('a', 'b')?.methods.length;
        const count: number = EXPORTS.length;
        const bytes: Uint8Array = tj.toMsgPack(1, tj.prim('INT32'), SCHEMAS);
        const shape: tj.Type = tj.seq('List', tj.prim(tj.Prim.STRING));
        const kind: 'Void' | 'Task' | 'TaskOf' | undefined = ChatScaffolding.method('Add')?.returnKind;
        const call: Promise<unknown> = runScript(host, ON_CONNECTED, [true], null, null, null);
        const slots: readonly string[] = ChatScaffolding.slots;
        void [nexus, result, item, json, codec, hash, found, count, bytes, shape, kind, call, slots];
        """;

    private static Dictionary<string, string> ChatSupport => new()
    {
        ["ext/money-formatter.js"] = """
            /** Test formatter of the C# Money struct: its cents. */
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

    private static Dictionary<string, string> InteropSupport => new()
    {
        ["interop-point-formatter.js"] = """
            /** Test formatter of the C# InteropPoint: [X, Y]. */
            export const InteropPointFormatter = Object.freeze({
              write(w, v) {
                w.writeArrayHeader(2);
                w.writeInt(v[0]);
                w.writeInt(v[1]);
              },
              read(r) {
                r.readArrayHeader(2);
                const x = r.readInt32();
                return [x, r.readInt32()];
              },
            });
            """,
    };

    private const string RunnerTemplate = """
import { readFileSync } from 'node:fs';
import { MsgPackReader, MsgPackWriter, NexusClient } from '@dtronix/nexnet';
import { InteropScaffolding, InteropScriptableNexus } from './scaffolding/InteropScaffolding.js';
import { EXPORTS, SCHEMA_HASH, find } from './scaffolding/index.js';
import { SCHEMAS, forType } from './scaffolding/test-codecs.js';
import * as tj from './scaffolding/typed-json.js';

const FACTS = __FACTS__;
let checked = 0;
let failed = 0;
let vectors = 0;
let echoes = 0;

function check(ok, what) {
  checked++;
  if (!ok) {
    failed++;
    console.log('FAIL ' + what);
  }
}

const show = (v) => JSON.stringify(v, (_, x) => (typeof x === 'bigint' ? x + 'n' : x));
const same = (a, b) => show(a) === show(b);

function fromHex(hex) {
  const out = new Uint8Array(hex.length / 2);
  for (let i = 0; i < out.length; i++) out[i] = parseInt(hex.slice(i * 2, i * 2 + 2), 16);
  return out;
}

function toHex(bytes) {
  let s = '';
  for (const b of bytes) s += (b < 16 ? '0' : '') + b.toString(16);
  return s;
}

function written(write) {
  const w = new MsgPackWriter();
  write(w);
  return w.toUint8Array().slice();
}

async function rejects(promise, type, what) {
  try {
    await promise;
    check(false, what + ': accepted');
  } catch (e) {
    check(e instanceof type, what + ': ' + e);
  }
}

// Echoes the first argument back through the result formatter, as the interop server's Echo methods do.
class EchoProxy extends InteropScaffolding.proxyType {
  _invoke(methodId, args, options) {
    if (!options?.returns) return Promise.resolve(undefined);
    const r = new MsgPackReader(written(args), { preset: 'trusted' });
    r.readArrayHeader();
    return Promise.resolve(options.returns.read(r));
  }
  _invokeVoid() {}
  _invokePipe() {
    return Promise.resolve();
  }
  _invokeChannel() {
    return Promise.resolve();
  }
  _collection(methodId, formatter, options) {
    return { methodId, formatter, options };
  }
}

const proxy = new EchoProxy({});
const ECHO = new Map();
for (const method of InteropScaffolding.methods) {
  if (method.name.startsWith('Echo') && method.parameters.length === 1 && method.result === method.parameters[0]) {
    ECHO.set(method.result.expression, method);
  }
}

// ------------------------------------------------------------------ golden vectors

const unrepresentable = new Set(FACTS.unrepresentable);
for (const file of ['primitives', 'containers', 'ext78', 'objects', 'unions']) {
  const text = readFileSync(FACTS.golden + '/' + file + '.jsonl', 'utf8');
  for (const line of text.split(/\r?\n/)) {
    if (line.trim() === '') continue;
    const v = JSON.parse(line);
    if (v.mode === 'reject' || v.options !== undefined || unrepresentable.has(v.id)) continue;
    if (v.hex.length > 4000 || JSON.stringify(v.value).length > 2000) continue;
    const type = v.type.replace(/[A-Za-z_][A-Za-z0-9_]*/g, (m) => FACTS.ids[m] ?? m);
    const codec = forType(type);
    if (codec === null) continue;
    vectors++;
    try {
      const value = codec.formatter.read(new MsgPackReader(fromHex(v.hex), { preset: 'trusted' }));
      const decoded = codec.toJson(value);
      check(same(decoded, v.value), v.id + ': decoded ' + show(decoded) + ', expected ' + show(v.value));
      if (v.mode === 'both') {
        const encoded = toHex(written((w) => codec.formatter.write(w, codec.fromJson(v.value))));
        check(encoded === v.hex, v.id + ': encoded ' + encoded + ', expected ' + v.hex);
      }
      const echo = ECHO.get(type);
      if (echo) {
        echoes++;
        const result = await InteropScaffolding.invokeByName(proxy, echo.name, [v.value]);
        check(same(result, v.value), v.id + ': ' + echo.name + ' returned ' + show(result));
      }
    } catch (e) {
      check(false, v.id + ': ' + (e && e.stack ? e.stack : e));
    }
  }
}

// ------------------------------------------------------------------ scriptable nexus

class Host {
  calls = [];
  reply = null;
  call(c) {
    this.calls.push(c);
    return this.reply;
  }
  get last() {
    return this.calls[this.calls.length - 1];
  }
}

const abort = new AbortController();
function context() {
  const ctx = {
    invocationId: 1,
    signal: abort.signal,
    result: null,
    writeResult(formatter, value) {
      ctx.result = toHex(written((w) => formatter.write(w, value)));
    },
    pipe(pipeId) {
      return { pipeId };
    },
    channel(pipeId, formatter) {
      return { pipeId, formatter };
    },
  };
  return ctx;
}

async function dispatch(nexus, name, write) {
  const ctx = context();
  await nexus._dispatch(FACTS.clientIds[name], new MsgPackReader(written(write)), ctx);
  return ctx.result;
}

const host = new Host();
const nexus = InteropScaffolding.createNexus(host);
check(nexus instanceof InteropScriptableNexus, 'createNexus');

host.reply = 'yo';
let result = await dispatch(nexus, 'ClientEchoString', (w) => { w.writeArrayHeader(1); w.writeString('hi'); });
check(host.last.slot === 'ClientEchoString' && same(host.last.args, ['hi']), 'ClientEchoString call ' + host.last);
check(host.last.signal === null && host.last.pipe === null, 'ClientEchoString signal');
check(result === 'a2796f', 'ClientEchoString result ' + result);

host.reply = null;
result = await dispatch(nexus, 'ClientDelay', (w) => { w.writeArrayHeader(2); w.writeInt(5); w.writeInt(7); });
check(host.last.slot === 'ClientDelay' && same(host.last.args, [5, 7]), 'ClientDelay call ' + host.last);
check(host.last.signal === abort.signal, 'ClientDelay signal');
check(result === '00', 'ClientDelay default result ' + result);

result = await dispatch(nexus, 'ClientNotify', (w) => { w.writeArrayHeader(1); w.writeString('note'); });
check(host.last.slot === 'ClientNotify' && same(host.last.args, ['note']) && result === null, 'ClientNotify ' + host.last + ' ' + result);

result = await dispatch(nexus, 'ClientPing', (w) => { w.writeArrayHeader(0); });
check(host.last.slot === 'ClientPing' && host.last.args.length === 0 && result === null, 'ClientPing ' + host.last);

result = await dispatch(nexus, 'ClientEchoPipe', (w) => { w.writeArrayHeader(1); w.writeInt(3); });
check(host.last.slot === 'ClientEchoPipe' && host.last.args.length === 0 && host.last.pipe.pipeId === 3 && result === null, 'ClientEchoPipe ' + host.last);

host.reply = [1, ['f64:0x4008000000000000', 'f64:0x4010000000000000']];
result = await dispatch(nexus, 'ClientEchoShape', (w) => { w.writeArrayHeader(1); w.writeNil(); });
check(same(host.last.args, [null]), 'ClientEchoShape null argument ' + host.last);
check(result === '920192cb4008000000000000cb4010000000000000', 'ClientEchoShape result ' + result);

host.reply = Promise.reject(new Error('scripted failure'));
let failure = null;
await dispatch(nexus, 'ClientEchoString', (w) => { w.writeArrayHeader(1); w.writeString('x'); }).catch((e) => { failure = e; });
check(failure !== null && failure.message === 'scripted failure', 'a failed script fails the call');

host.reply = null;
host.call = () => { throw new Error('host threw'); };
failure = null;
await dispatch(nexus, 'ClientPing', (w) => { w.writeArrayHeader(0); }).catch((e) => { failure = e; });
check(failure !== null && failure.message === 'host threw', 'a throwing host fails the call');
await dispatch(nexus, 'ClientNotify', (w) => { w.writeArrayHeader(1); w.writeString('n'); });
nexus.onConnected(false);
check(true, 'fire-and-forget slots drop host errors');
delete host.call;

nexus.onConnected(true);
check(host.last.slot === 'OnConnected' && same(host.last.args, [true]), 'OnConnected ' + host.last);
nexus.onDisconnected(0);
check(host.last.slot === 'OnDisconnected' && host.last.args.length === 0, 'OnDisconnected ' + host.last);
check(InteropScaffolding.slots.includes('ClientEchoString') && InteropScaffolding.slots.includes('OnConnected')
  && InteropScaffolding.slots.includes('OnDisconnected'), 'slots ' + InteropScaffolding.slots);

// ------------------------------------------------------------------ exports

check(SCHEMA_HASH === FACTS.schemaHash, 'schema hash ' + SCHEMA_HASH);
check(find('NexNet.TestNexus.Interop.IInteropClientNexus', 'NexNet.TestNexus.Interop.IInteropServerNexus') === InteropScaffolding, 'find');
check(find('x', 'y') === null, 'find unknown');
check(EXPORTS.length === FACTS.exports, 'exports ' + EXPORTS.length);
check(InteropScaffolding.method('EchoInt64Channel').channelItem === forType('long'), 'channel item codec');
check(InteropScaffolding.method('EchoInt64Channel').pipeKind === 'Channel', 'channel kind');
check(InteropScaffolding.method('EchoPipe').pipeKind === 'Pipe', 'pipe kind');
check(InteropScaffolding.method('Delay').cancellable, 'Delay cancellable');
check(InteropScaffolding.method('Notify').returnKind === 'Void', 'Notify void');
check(InteropScaffolding.method('Nope') === null, 'unknown method lookup');
check(InteropScaffolding.collection('Items').item === forType('NexNet.TestNexus.Interop.InteropItem'), 'Items codec');
check(InteropScaffolding.list(proxy, 'Items').formatter === forType('NexNet.TestNexus.Interop.InteropItem').formatter, 'Items list');
let unknownList = null;
try { InteropScaffolding.list(proxy, 'Nope'); } catch (e) { unknownList = e; }
check(unknownList instanceof TypeError, 'unknown collection');

result = await InteropScaffolding.invokeByName(proxy, 'Delay', [10, 42], { signal: abort.signal });
check(result === 10, 'Delay echo ' + result);
result = await InteropScaffolding.invokeByName(proxy, 'Notify', ['x']);
check(result === null, 'Notify result ' + result);
result = await InteropScaffolding.invokeByName(proxy, 'Ping', []);
check(result === null, 'Ping result ' + result);
result = await InteropScaffolding.invokeByName(proxy, 'EchoStruct', [null]);
check(same(result, tj.defaultValue(forType('NexNet.TestNexus.Interop.InteropStruct').type, SCHEMAS)), 'EchoStruct default ' + show(result));
await rejects(InteropScaffolding.invokeByName(proxy, 'Nope', []), TypeError, 'unknown method');
await rejects(InteropScaffolding.invokeByName(proxy, 'EchoInt32', []), TypeError, 'missing argument');
await rejects(InteropScaffolding.invokeByName(proxy, 'EchoByte', [256]), RangeError, 'byte range');
await rejects(InteropScaffolding.invokeByName(proxy, 'EchoInt32', ['x']), TypeError, 'not an integer');
await rejects(InteropScaffolding.invokeByName(proxy, 'EchoPipe', [], { pipe: 'not a pipe' }), TypeError, 'pipe check');
await rejects(InteropScaffolding.invokeByName(proxy, 'EchoInt64Channel', [], { pipe: null }), TypeError, 'channel check');

const client = InteropScaffolding.createClient({ webSocket: { url: 'ws://localhost:1' } }, new Host());
check(client instanceof NexusClient && client.nexus instanceof InteropScriptableNexus, 'createClient');

check(tj.base64(tj.fromBase64('AAECAwQ=')) === 'AAECAwQ=', 'base64');
check(tj.base64(new Uint8Array(0)) === '' && tj.fromBase64('').length === 0, 'base64 empty');
check(tj.fromBase64('/w==')[0] === 0xff, 'base64 padding');
let badBase64 = null;
try { tj.fromBase64('abc'); } catch (e) { badBase64 = e; }
check(badBase64 instanceof TypeError, 'invalid base64');

console.log('vectors=' + vectors + ' echoes=' + echoes);
console.log('checked=' + checked + ' failed=' + failed);
if (failed > 0) process.exit(1);
""";

    /// <summary>A temporary directory with the client and scaffolding files, the support files and the runtime package linked in.</summary>
    private sealed class ScaffoldWorkspace : IDisposable
    {
        private ScaffoldWorkspace(string root) => Root = root;

        public string Root { get; }

        public static async Task<ScaffoldWorkspace> Create(JsRun client, JsScaffoldRun scaffolding, IReadOnlyDictionary<string, string> support)
        {
            var root = Path.Combine(Path.GetTempPath(), "nexnet-j11-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var workspace = new ScaffoldWorkspace(root);
            foreach (var (path, content) in client.Files.Concat(scaffolding.Files).Concat(support))
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
}
