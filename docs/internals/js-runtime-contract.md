# JavaScript runtime contract (`@dtronix/nexnet`)

Status: introduced by the multi-language train (unit J01). This is the contract between the code the generator emits
for JavaScript (J03, J04, J11) and the hand-written runtime (J02, J05–J10). It is **frozen**: renaming or removing a
member, or changing a signature, needs a contract amendment card that updates every consumer in one PR. Adding an
optional member or an optional config property is not a contract change.

Source of truth: the JSDoc in `clients/js/packages/nexnet/src/contract/*.js`. This page explains the shape and the rules;
it does not repeat every member.

## 1. Package and workspace

| Path | Contents |
|---|---|
| `clients/js/package.json` | Private npm workspace root: dev dependencies and scripts |
| `clients/js/packages/nexnet/` | `@dtronix/nexnet`, version in lockstep with the NexNet NuGet package (D19) |
| `clients/js/tsconfig.json` | Type check and `.d.ts` emit for the runtime (`allowJs`, `checkJs`, `strict`) |
| `clients/js/tsconfig.test.json` | Type check of the runtime plus tests and config files |
| `clients/js/vitest.config.js` | Vitest projects `node` and `browser` (Playwright, Chromium) |
| `clients/js/eslint.config.js` | ESLint flat config |

The package is ESM only (`"type": "module"`), has `"sideEffects": false`, requires Node 22 or later and has **no runtime
dependencies**. The sources are plain `.js` with JSDoc; `.d.ts` files are generated into `dist/` (gitignored).

### Scripts (run in `clients/js`)

| Script | Does |
|---|---|
| `npm ci` | Installs the pinned dev dependencies |
| `npm run lint` | ESLint |
| `npm run typecheck` | `tsc --noEmit` over the runtime, then over runtime + tests |
| `npm test` | Vitest, `node` project |
| `npm run test:browser` | Vitest, `browser` project in headless Chromium (run `npx playwright install chromium` once) |
| `npm run build:types` | Emits `packages/nexnet/dist/**/*.d.ts` |

The JS gate (train §4.3) is `npm ci`, `npm test` and `npm run typecheck` with `--prefix clients/js`. None of them needs a
browser download.

### Entry points

```json
"exports": {
  ".":      { "types": "./dist/index.d.ts", "browser": "./src/browser.js", "node": "./src/node.js", "default": "./src/index.js" },
  "./node": { "types": "./dist/node.d.ts",  "node": "./src/node.js" }
}
```

| File | Role |
|---|---|
| `src/index.js` | Platform-neutral API: everything generated code and applications use |
| `src/browser.js` | Browser entry. Re-exports `index.js`; the place for browser-only additions (Worker pings, lifecycle reconnect) |
| `src/node.js` | Node entry. Re-exports `index.js`, adds `NodeNexusClient` and exports it **as** `NexusClient` |

So `import { NexusClient } from '@dtronix/nexnet'` gives the Node-capable client under Node and the WebSocket-only client
in a browser bundle, under the same name. Generated code imports only `@dtronix/nexnet` (or the module name set by the
`ModuleName` target option).

**Browser purity rule.** Nothing reachable from `src/browser.js` or `src/index.js` may import `node:*` or a bare Node
built-in, statically or through a literal dynamic `import()`. `test/entry-purity.test.js` walks the import graph and
fails on any such import, and on any non-relative import at all (zero runtime dependencies). Node-only code lives in
`src/node.js`, `src/contract/node.js` and directories named `node/` (for example `src/transport/node/`), which only the
Node entry imports. ESLint enforces the matching globals: Node globals are visible only in those files, tests and config.

## 2. Ownership and how implementations replace the skeleton

J01 publishes the skeleton in `src/contract/`. Every non-trivial body throws
`new Error('not implemented: <Class.member>')`. The implementation cards own these locations:

| Contract file | Implementing card | Implementation location |
|---|---|---|
| `codec.js`, `values.js` | J02 (finalizes the formatter API) | `src/serialization/`, `src/values/` |
| `client.js`, `transport.js`, `config.js` | J05 | `src/client/NexusClient.js`, `src/session/`, `src/transport/ITransport.js` |
| transport stubs (`unimplementedTransport`), `node.js` | J06 | `src/transport/` |
| `proxy.js`, `nexus.js` | J07 | `src/client/ProxyBase.js`, `src/client/ClientNexusBase.js`, `src/invocation/` |
| `pipes.js` | J08 | `src/pipes/` |
| `collections.js` | J09 | J09's collections directory |
| `pool.js` | J10 | `src/pool/` |

An implementation card moves the class into its directory with the **same name, members and signatures**, and turns
the contract file into a re-export (`export { NexusClient } from '../client/NexusClient.js';`). The entry points and
`test/contract.test.js` (which pins the exported names) keep passing. A card that needs a different signature asks for
an amendment instead.

`errors.js` and `enums.js` are complete, not skeletons.

## 3. API

### 3.1 Codec (`codec.js`)

- `Formatter<T>` is `{ write(writer, value): void, read(reader): T }`, a stateless plain object.
- `MsgPackWriter`: canonical (smallest) MessagePack into a growable buffer. `writeNil`, `writeBool`, `writeInt`
  (safe-integer `number`), `writeInt64`/`writeUInt64` (`bigint`), `writeFloat32`, `writeFloat64`, `writeString`,
  `writeBin`, `writeArrayHeader`, `writeMapHeader`, `writeExt`, `writeRaw`, `length`, `toUint8Array`, `reset`.
- `MsgPackReader`: over one `Uint8Array` or a list of segments, hardened by `SerializerOptions` (`preset`, `maxDepth`,
  `maxElements`, `strictUtf8`). `tryReadNil`, `readBool`, `readInt`, `readInt64`, `readUInt64`, `readFloat`,
  `readString`, `readBin`, `readArrayHeader`, `readMapHeader`, `readExt`, `skip`, `remaining`.
- `FormatterRegistry`: `register(key, formatter)`, `get(key)`, `tryGet(key)`.
- `tupleFormatter(...formatters)`: tuples are JS arrays.

J02 adds the built-in formatters and may add reader/writer members (for example ext 78 primitive arrays); it may not
rename the ones above.

### 3.2 Value types (`values.js`)

Names match the G03 JavaScript type mapper (`src/NexNet.Generator/Targets/JavaScript/JavaScriptTarget.cs`).

| C# | JS | Fields |
|---|---|---|
| `decimal` | `NexusDecimal` | `magnitude: bigint` (96-bit), `scale`, `negative` |
| `DateTime` | `NexusDateTime` | `ticks: bigint`, `kind` (`DateTimeKind`) |
| `DateTimeOffset` | `NexusDateTimeOffset` | `ticks: bigint` (clock ticks), `offsetMinutes` |
| `TimeSpan` | `NexusTimeSpan` | `ticks: bigint` |
| `DateOnly` | `NexusDateOnly` | `dayNumber` |
| `TimeOnly` | `NexusTimeOnly` | `ticks: bigint` |
| `Version` | `NexusVersion`, also exported as **`Version`** (the name the type mapper emits) | `major`, `minor`, `build`, `revision` (-1 when absent) |
| tuples | arrays; `tuple(...items)` fixes the TS tuple type | |

Fields are public and `readonly`. Each type has `equals(other)` and conversions (`toDate`/`fromDate`, `parse`,
`toString`, …), implemented by J02.

### 3.3 Client (`client.js`, `config.js`, `transport.js`)

`NexusClient<TNexus, TProxy> extends EventTarget`:

- `static create(config, nexus, binding)` creates an instance of the class it is called on. `binding` is a
  `ClientBinding` (§4) that the generated `create<Export>Client(config, nexus)` supplies.
- `connect({signal})`, `tryConnect({signal})` → `ConnectionResult` (`success`, `state`, `disconnectReason`, `error`),
  `disconnect()`, `[Symbol.asyncDispose]()`.
- `state` (`ConnectionState`), `proxy`, `isSecure`, `disconnected` (a promise), `config`, `nexus`.
- `createPipe()`, `createChannel(formatter)`, `getSnapshot()`.
- Events: `statechange` and `disconnected`.
- `static transports`: the transport factories the entry supports, keyed by transport kind. `NexusClient` (core and
  browser) has `webSocket`; `NodeNexusClient` adds `httpSocket`, `tcp`, `tcpTls` and `uds`.

`ClientConfig` is a plain object: exactly one transport property (`webSocket`, `httpSocket`, `tcp`, `tcpTls`, `uds`)
plus the common options, named as the C# `ClientConfig`/`ConfigBase` properties in camelCase, durations in
milliseconds. `authenticate({signal})` may return the token synchronously or as a promise. It runs before the transport
connects, on every connect and reconnect, bounded by the handshake timeout; if it throws, the connect fails with
`DisconnectReason.Authentication` and no connection is opened (F05 semantics).

`isSecure` is true only for transports that declare themselves secure: TLS, WSS, HTTPS and (Node) UDS. Over any other
transport a client logs `NexNet: unencrypted transport (<transport>); credentials and data are visible on the network.`
once per instance, on the first connect (D7).

The `Transport` interface between the session and the transports: `write(chunks)`, `onData(cb)`, `onClose(cb)`,
`close(reason)`, `isSecure`, `remoteCertificate`, `name`. A `TransportFactory` is `(config, {signal}) => Promise<Transport>`.

### 3.4 Generated-code base classes (`proxy.js`, `nexus.js`)

`ProxyBase` (base of `<Server>Proxy`); all members are protected:

| Member | C# method shape |
|---|---|
| `_invoke(methodId, args, {signal, returns})` → `Promise<T>` | `ValueTask` (no `returns`) and `ValueTask<T>` |
| `_invokeVoid(methodId, args)` → `void` | `void` (fire-and-forget; queued for credit, never dropped) |
| `_invokePipe(methodId, pipe, args, {signal})` → `Promise<void>` | `INexusDuplexPipe` parameter |
| `_invokeChannel(methodId, channel, args, {signal})` → `Promise<void>` | `INexusDuplexChannel<T>`/`INexusDuplexUnmanagedChannel<T>` parameter |
| `_collection(methodId, formatter, {equals})` → `NexusList<T>` | collection property |

`args` is an `ArgumentsWriter` `(writer) => void` that writes **one MessagePack array** with an element per C#
parameter in declaration order, excluding the `CancellationToken`; `null` when there are no parameters. For pipe and
channel methods it is a `PipeArgumentsWriter` `(writer, pipeId) => void` that writes the pipe or channel parameter as
`pipeId` at its position.

`ClientNexusBase<TProxy>` (base of `<Client>NexusBase`):

- hooks `onConnected(isReconnected)`, `onDisconnected(reason)` and `onReconnecting()`, no-ops by default, may be async;
- `context`: a `ClientContext` with `proxy`, `sessionId`, `createPipe`, `createChannel` and `disconnect`;
- `_dispatch(methodId, reader, ctx)`: the generated override switches on `methodId`, reads the arguments array from
  `reader`, calls the method and reports a `ValueTask<T>` result with `ctx.writeResult(formatter, value)`. `ctx` is a
  `DispatchContext`: `invocationId`, `signal` (the C# `CancellationToken`), `writeResult`, `pipe(pipeId)` and
  `channel(pipeId, formatter)`. Unknown IDs fall through to `super._dispatch`, which throws. The runtime checks
  `methodId` against `acceptedIds` before dispatching.

### 3.5 Pipes, channels, collections, pool

- `NexusDuplexPipe`: `id`, `readable` (`ReadableStream<Uint8Array>`), `writable` (`WritableStream<Uint8Array>`), `ready`,
  `completed`, `complete()`, `[Symbol.asyncDispose]`.
- `NexusDuplexChannel<T>`: `pipe`, `getReader()`, `getWriter()`, `complete()`, `[Symbol.asyncDispose]`.
- `NexusChannelReader<T>`: `read()` → `IteratorResult<T>`, `readBatch(max)`, `readUntilComplete()`,
  `[Symbol.asyncIterator]`, `isComplete`. `NexusChannelWriter<T>`: `write`, `writeMany`, `complete`, `writeAndComplete`.
- `NexusList<T> extends EventTarget` (event `changed`): `state`, `isEnabled`, `ready`, `disabled`, `count`, `get`,
  `indexOf`, `contains`, `toArray`, iterator, `enable`, `disable`, and the `Promise<boolean>` mutations `add`,
  `insert`, `remove`, `removeAt`, `replace`, `move` and `clear`.
- `NexusClientPool<TNexus, TProxy>`: `new NexusClientPool(config, nexusFactory, binding)`, `maxConnections`,
  `availableConnections`, `rent({signal})` → `RentedNexusClient`, `collectionConnector(selector)`,
  `[Symbol.asyncDispose]`.

### 3.6 Errors, cancellation and enums

| Error | Raised when |
|---|---|
| `NexusError` | Base class |
| `NexusTransportError` (`reason`) | Transport failure (for example, not connected). For an invocation pending when the session ends it is the `cause` of an `AbortError` (below) |
| `NexusRemoteInvocationError` (`code`, `remoteType`, `remoteMessage`) | The remote method threw |
| `NexusUnauthorizedError` | The server rejected the call as unauthorized |
| `NexusBusyError` | A pipe, channel or large-message limit refused the operation |
| `NexusMessageTooLargeError` | A message exceeded the negotiated `MaxMessageSize` |

Cancellation (D17) is not a NexNet error class: operations reject with `cancellationError(signal)`, which is
`signal.reason` when set and otherwise a `DOMException` named `AbortError`. `isCancellationError(e)` is true for
`AbortError` and `TimeoutError`.

**Session end (amendment C-2).** An invocation still pending when the session ends (waiting for credit, streaming,
or waiting for its result) completes as canceled, as in C# (`TaskCanceledException`) and protocol spec §4.5: it
rejects with a `DOMException` named `AbortError` whose `cause` is a `NexusTransportError` carrying the disconnect
`reason`. A call made while not connected still rejects with `NexusTransportError` directly.

`ConnectionState`, `ConnectionResultState`, `DisconnectReason` (wire codes 20–38), `RemoteExceptionDetail`,
`NexusCollectionState` and `DateTimeKind` are frozen objects with the C# values.

## 4. Handshake descriptor and binding

Generated code passes one `ClientBinding` per export to the runtime:

```js
export const ChatHandshake = Object.freeze({
  version: null,                 // string (at most 16 chars) for a versioned server nexus, else null
  serverNexusHash: -1288190245,  // from ExportIr.Handshake; never recomputed
  clientNexusHash: 401298355,
  acceptedIds: new Set([0, 1]),  // BigUint64Array | Set<number>
});

export function createChatClient(config, nexus) {
  return NexusClient.create(config, nexus, { handshake: ChatHandshake, proxy: ChatServerProxy });
}
```

`test/fixtures/generated-sample.js` is a hand-written sample of the full generated shape: a proxy with value, void,
pipe and collection members, a client nexus base with `_dispatch`, and the factory. The tests type-check and exercise
it, so a contract change that breaks generated code fails the JS gate.

## 5. Tooling versions

Dev dependencies are pinned exactly, each to a release at least two weeks old when pinned: `typescript`, `vitest`,
`@vitest/browser`, `@vitest/browser-playwright` (the Playwright provider package), `playwright`, `vite` (Vitest's
peer), `eslint`, `@eslint/js`, `globals` and `@types/node`. `package-lock.json` is committed; install with `npm ci`.
`clients/js/package.json` and `clients/js/packages/nexnet/package.json` are hot files (train §4.4).
