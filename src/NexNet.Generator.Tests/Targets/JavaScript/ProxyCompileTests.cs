using System.Globalization;
using System.Text.Json;

namespace NexNet.Generator.Tests.Targets.JavaScript;

/// <summary>
/// SDK-gated checks of the export module (J04): <c>tsc --noEmit</c> on its declarations with a TypeScript file that
/// implements the client nexus and calls the proxy, and a Node runner that drives the generated proxy, client nexus
/// dispatch, handshake and factory against the runtime package, comparing bytes with the C# serializer's. Skipped
/// without <c>NEXNET_REQUIRE_SDKS=1</c> or <c>NEXNET_TEST_LANGS=node</c>.
/// </summary>
public class JsProxyCompileTests
{
    private static readonly SemaphoreSlim NodeLock = new(2, 2);

    [Test]
    public async Task ChatSamplesSerializeInCSharp()
    {
        var samples = Samples();
        await Assert.That(samples.Count).IsGreaterThan(15);
        await Assert.That(Runner()).DoesNotContain("__");
    }

    [Test]
    public async Task ChatDeclarationsTypeCheck()
    {
        var sdk = await NodeSdk.Get();
        using var workspace = await Workspace.Create(JsProxyEmitterTests.Chat);
        await workspace.Write("usage.ts", Usage);
        var files = JsProxyEmitterTests.Chat.Files.Keys.Where(f => f.EndsWith(".d.ts", StringComparison.Ordinal))
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

    [Test]
    public async Task ChatModuleRunsAgainstTheRuntime()
    {
        var sdk = await NodeSdk.Get();
        using var workspace = await Workspace.Create(JsProxyEmitterTests.Chat);
        var runner = Runner();
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
        var count = runner.Split('\n').Sum(l => CountOf(l, "check("));
        await Assert.That(result.Output).Contains("checked=" + (count - 1).ToString(CultureInfo.InvariantCulture) + " failed=0");
    }

    private static int CountOf(string text, string what)
    {
        var count = 0;
        for (var i = text.IndexOf(what, StringComparison.Ordinal); i >= 0; i = text.IndexOf(what, i + what.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    /// <summary>The C# argument and result bytes of <c>Chat.ArgumentSamples</c>, as hex.</summary>
    private static Dictionary<string, string> Samples()
    {
        var assembly = JsProxyEmitterTests.Chat.Load();
        var all = (Dictionary<string, byte[]>)assembly.GetType("Chat.ArgumentSamples")!.GetMethod("All")!.Invoke(null, null)!;
        return all.ToDictionary(p => p.Key, p => Convert.ToHexString(p.Value).ToLowerInvariant());
    }

    /// <summary>The runner with the C# samples and the IR hashes substituted for its <c>__NAME__</c> placeholders.</summary>
    private static string Runner()
    {
        var text = RunnerSource;
        foreach (var (name, hex) in Samples())
            text = text.Replace("__" + name + "__", hex);
        var handshake = JsProxyEmitterTests.Chat.Ir!.Exports.Single().Handshake;
        return text.Replace("__SERVER_HASH__", handshake.ServerNexusHash.ToString(CultureInfo.InvariantCulture))
            .Replace("__CLIENT_HASH__", handshake.ClientNexusHash.ToString(CultureInfo.InvariantCulture));
    }

    private const string Usage = """
        import { NexusList, NexusDuplexPipe, NexusDuplexChannel, NexusClient } from '@dtronix/nexnet';
        import { ChatClientBinding, ChatClientNexusBase, ChatHandshake, ChatServerProxy, createChatClient } from './ChatServerNexus.js';
        import { Box } from './Box.js';
        import { Color } from './Color.js';
        import { Item } from './Item.js';

        class MyNexus extends ChatClientNexusBase {
          protected square(value: number, cancellationToken: AbortSignal): number {
            return cancellationToken.aborted ? 0 : value * value;
          }
          protected message(text: string, money: unknown): void {}
          protected async receive(pipe: NexusDuplexPipe): Promise<void> {
            await pipe.completed;
          }
          protected feed(count: number, channel: NexusDuplexChannel<Item>): void {}
          protected async heavy(): Promise<void> {}
          protected async context_(box: Box<string>, item: Item | null): Promise<Item[] | null> {
            return item === null ? null : [item];
          }
          protected onConnected_(arguments_: number, context: number, methodId: number): void {}
          protected pick(colors: Color[], map: Map<number, string> | null): Color {
            return colors[0] ?? Color.Red;
          }
        }

        // @ts-expect-error a client nexus must implement every method
        class Incomplete extends ChatClientNexusBase {}

        export const client: NexusClient<MyNexus, ChatServerProxy> = createChatClient({ webSocket: { url: 'ws://localhost:1' } }, new MyNexus());
        export const nexus: MyNexus = client.nexus;
        export const hash: number = ChatHandshake.serverNexusHash;
        export const proxyClass: typeof ChatServerProxy = ChatClientBinding.proxy;

        export async function use(proxy: ChatServerProxy, pipe: NexusDuplexPipe, channel: NexusDuplexChannel<Item>): Promise<void> {
          proxy.notify('hi');
          await proxy.ping();
          const sum: number = await proxy.add(1, 2, { signal: AbortSignal.timeout(100) });
          const sum2: number = await proxy.add(1, 2);
          const found: string | null = await proxy.find(null, 'x');
          const items: Item[] = await proxy.items(new Box<number>({ value: 1 }), null, Color.Green, 1, 5n, [new Item()]);
          await proxy.upload(pipe, 'name');
          await proxy.stream(channel);
          const waited: bigint = await proxy.wait({ signal: new AbortController().signal });
          const names: NexusList<string> = proxy.names;
          const boxes: NexusList<Box<number>> = proxy.boxes;
          const scores: NexusList<number> = proxy.scores;
          // @ts-expect-error a string is not a number
          await proxy.add('a', 2);
          // @ts-expect-error the method has no cancellation
          await proxy.ping({ signal: AbortSignal.timeout(1) });
          void [sum, sum2, found, items, waited, names, boxes, scores, Incomplete];
        }
        """;

    private const string RunnerSource = """
        import { MsgPackWriter, MsgPackReader, NexusClient, NexusCollectionMode, stringFormatter, int32Formatter, int32ArrayFormatter, binFormatter, byteFormatter } from '@dtronix/nexnet';
        import { ChatClientBinding, ChatClientNexusBase, ChatHandshake, ChatServerProxy, createChatClient } from './ChatServerNexus.js';
        import { ItemFormatter } from './formatters.js';
        import { Box } from './Box.js';
        import { Color } from './Color.js';
        import { Item } from './Item.js';

        let checked = 0;
        let failed = 0;
        function check(name, ok, detail) {
          checked++;
          if (!ok) {
            failed++;
            console.log('FAIL ' + name + (detail === undefined ? '' : ': ' + detail));
          }
        }
        const hex = (bytes) => Buffer.from(bytes).toString('hex');
        const bytes = (text) => new Uint8Array(Buffer.from(text, 'hex'));
        const reader = (text) => new MsgPackReader(bytes(text));
        function written(write, ...extra) {
          const w = new MsgPackWriter();
          write(w, ...extra);
          return hex(w.toUint8Array());
        }

        // ------------------------------------------------------------------ proxy

        class Recording extends ChatServerProxy {
          calls = [];
          _invoke(methodId, args, options) {
            this.calls.push({ kind: 'invoke', methodId, args: args === null ? null : written(args), options });
            return Promise.resolve('pending');
          }
          _invokeVoid(methodId, args) {
            this.calls.push({ kind: 'void', methodId, args: args === null ? null : written(args) });
          }
          _invokePipe(methodId, pipe, args, options) {
            this.calls.push({ kind: 'pipe', methodId, pipe, args: written(args, 7), options });
            return Promise.resolve();
          }
          _invokeChannel(methodId, channel, args, options) {
            this.calls.push({ kind: 'channel', methodId, channel, args: written(args, 7), options });
            return Promise.resolve();
          }
          _collection(methodId, formatter, options) {
            return { methodId, formatter, options };
          }
        }

        const proxy = new Recording({});
        const last = () => proxy.calls[proxy.calls.length - 1];
        const signal = new AbortController().signal;

        check('notify returns nothing', proxy.notify('hi') === undefined);
        check('notify', last().kind === 'void' && last().methodId === 0 && last().args === '__NOTIFY__', JSON.stringify(last()));
        await proxy.ping();
        check('ping', last().kind === 'invoke' && last().methodId === 1 && last().args === null && last().options === undefined);
        await proxy.add(1, 2, { signal });
        check('add', last().methodId === 2 && last().args === '__ADD__' && last().options.signal === signal, JSON.stringify(last()));
        check('add result', last().options.returns.read(reader('__THREE__')) === 3);
        await proxy.add(1, 2);
        check('add without options', last().options.signal === undefined);
        await proxy.find(null, 'x');
        check('find', last().methodId === 3 && last().args === '__FIND__', last().args);
        await proxy.items(new Box({ value: 5 }), new Map([['k', null]]), Color.Green, 200, 42n, [new Item({ id: 1, name: 'a' })]);
        check('items', last().methodId === 4 && last().args === '__ITEMS__', last().args);
        const items = last().options.returns.read(reader('__ITEMS_RESULT__'));
        check('items result', items.length === 1 && items[0] instanceof Item && items[0].id === 2 && items[0].name === 'b');
        const pipe = { id: 0 };
        await proxy.upload(pipe, 'n');
        check('upload', last().kind === 'pipe' && last().methodId === 5 && last().pipe === pipe && last().args === '__UPLOAD__', last().args);
        const channel = { pipe };
        await proxy.stream(channel);
        check('stream', last().kind === 'channel' && last().methodId === 6 && last().channel === channel && last().args === '__STREAM__', last().args);
        await proxy.invoke(3, 4);
        check('invoke', last().methodId === 7 && last().args === '__INVOKE__');
        await proxy.constructor_();
        check('constructor_', last().methodId === 8 && last().args === null);
        await proxy.wait();
        check('wait', last().methodId === 9 && last().options.returns.read(reader('__WAIT_RESULT__')) === 9000000000n);
        await proxy.search('q', { signal });
        check('search', last().methodId === 10 && last().args === '__SEARCH__' && last().options.signal === signal, last().args);
        check('names', proxy.names.methodId === 11 && proxy.names.formatter === stringFormatter
          && proxy.names.options.mode === NexusCollectionMode.BiDirectional && proxy.names.options.valuesFormatter === undefined);
        check('list', proxy.list.formatter === ItemFormatter && proxy.list.options.mode === NexusCollectionMode.ServerToClient);
        check('boxes', proxy.boxes.methodId === 13 && proxy.boxes.options.mode === NexusCollectionMode.Relay);
        check('scores', proxy.scores.formatter === int32Formatter && proxy.scores.options.valuesFormatter === int32ArrayFormatter);
        check('bytes', proxy.bytes.formatter === byteFormatter && proxy.bytes.options.valuesFormatter === binFormatter);

        // ------------------------------------------------------------------ client nexus

        class Nexus extends ChatClientNexusBase {
          calls = [];
          releaseReceive = null;
          square(value, token) {
            this.calls.push(['square', value, token]);
            return value * value;
          }
          message(text, money) {
            this.calls.push(['message', text, money]);
          }
          receive(pipe) {
            this.calls.push(['receive', pipe]);
            return new Promise((resolve) => { this.releaseReceive = resolve; });
          }
          async feed(count, channel) {
            this.calls.push(['feed', count, channel]);
          }
          async heavy() {
            this.calls.push(['heavy']);
          }
          async context_(box, item) {
            this.calls.push(['context', box, item]);
            return null;
          }
          onConnected_(a, b, c) {
            this.calls.push(['onConnected_', a, b, c]);
          }
          pick(colors, map) {
            this.calls.push(['pick', colors, map]);
            return Color.Green;
          }
        }

        const nexus = new Nexus();
        const lastCall = () => nexus.calls[nexus.calls.length - 1];
        const abort = new AbortController();
        function context() {
          const ctx = {
            invocationId: 1,
            signal: abort.signal,
            result: null,
            writeResult(formatter, value) { ctx.result = written((w) => formatter.write(w, value)); },
            pipe(pipeId) { return { pipeId }; },
            channel(pipeId, formatter) { return { pipeId, formatter }; },
          };
          return ctx;
        }

        let ctx = context();
        await nexus._dispatch(0, reader('__SQUARE__'), ctx);
        check('square', lastCall()[1] === 5 && lastCall()[2] === abort.signal && ctx.result === '__SQUARED__', ctx.result);
        ctx = context();
        await nexus._dispatch(1, reader('__MESSAGE__'), ctx);
        check('message', lastCall()[1] === 't' && lastCall()[2] === 3n && ctx.result === null);

        // C-3: the pipe stays the method's until its promise settles.
        ctx = context();
        let settled = false;
        const receiving = nexus._dispatch(2, reader('__RECEIVE__'), ctx).then(() => { settled = true; });
        await new Promise((resolve) => setTimeout(resolve, 10));
        check('receive pipe', lastCall()[0] === 'receive' && lastCall()[1].pipeId === 9);
        check('receive waits for the method', !settled);
        nexus.releaseReceive();
        await receiving;
        check('receive completes after the method', settled);

        await nexus._dispatch(3, reader('__FEED__'), context());
        check('feed', lastCall()[1] === 2 && lastCall()[2].pipeId === 8 && lastCall()[2].formatter === ItemFormatter);
        await nexus._dispatch(4, new MsgPackReader(new Uint8Array(0)), context());
        check('heavy', lastCall()[0] === 'heavy');
        ctx = context();
        await nexus._dispatch(5, reader('__CONTEXT__'), ctx);
        check('context', lastCall()[1] instanceof Box && lastCall()[1].value === 'b' && lastCall()[2].id === 1 && lastCall()[2].name === 'n'
          && ctx.result === '__CONTEXT_RESULT__', ctx.result);
        await nexus._dispatch(6, reader('__ON_CONNECTED__'), context());
        check('onConnected_', lastCall().slice(1).join() === '1,2,3');
        ctx = context();
        await nexus._dispatch(7, reader('__PICK__'), ctx);
        check('pick', lastCall()[1][0] === Color.Green && lastCall()[2].get(1) === 'x' && ctx.result === '__GREEN__', ctx.result);
        check('separate pool', nexus._separatePool(4) && nexus._separatePool(5) && !nexus._separatePool(0));

        let unknown = null;
        await nexus._dispatch(99, reader('90'), context()).catch((e) => { unknown = e; });
        check('unknown method id rejects', unknown !== null);

        class Partial extends ChatClientNexusBase {}
        let missing = null;
        await new Partial()._dispatch(4, new MsgPackReader(new Uint8Array(0)), context()).catch((e) => { missing = e; });
        check('missing method rejects', missing !== null && String(missing.message).includes('ChatClientNexusBase.heavy is not implemented'));

        let abstract = null;
        try { new ChatClientNexusBase(); } catch (e) { abstract = e; }
        check('the base class is abstract', abstract instanceof TypeError);

        // ------------------------------------------------------------------ handshake and factory

        check('handshake', ChatHandshake.version === null && ChatHandshake.serverNexusHash === __SERVER_HASH__
          && ChatHandshake.clientNexusHash === __CLIENT_HASH__ && ChatHandshake.acceptedIds.has(7) && !ChatHandshake.acceptedIds.has(8));
        check('binding', ChatClientBinding.handshake === ChatHandshake && ChatClientBinding.proxy === ChatServerProxy);
        const client = createChatClient({ webSocket: { url: 'ws://localhost:1' } }, nexus);
        check('factory', client instanceof NexusClient && client.nexus === nexus);

        console.log('checked=' + checked + ' failed=' + failed);
        if (failed > 0) process.exit(1);
        """;

    private static Dictionary<string, string> Support() => new()
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

    private sealed class Workspace : IDisposable
    {
        private Workspace(string root) => Root = root;

        public string Root { get; }

        /// <summary>A temporary directory with the emitted files, the money formatter and the runtime package linked in.</summary>
        public static async Task<Workspace> Create(JsRun run)
        {
            await Assert.That(run.Diagnostics.Select(d => d.ToString())).IsEmpty();
            var root = Path.Combine(Path.GetTempPath(), "nexnet-j04-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var workspace = new Workspace(root);
            foreach (var (path, content) in run.Files.Concat(Support()))
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
