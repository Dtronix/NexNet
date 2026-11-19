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
| `src/browser.js` | Browser entry. Re-exports `index.js`; the place for browser-only additions (lifecycle reconnect). The Worker keepalive (§3.3) is feature-detected in platform-neutral code, not added here |
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
- Events (`CustomEvent`, dispatched from a microtask, never re-entrantly from a transport callback):
  - `statechange`, `detail: { state, previous }` (both `ConnectionState` values), on every change of `state`;
  - `disconnected`, `detail: { reason }` (a `DisconnectReason`), once per session that completed its handshake, just
    before the nexus's `onDisconnected(reason)` hook. A connect that fails before the handshake completes is reported
    only through the `connect`/`tryConnect` result, not through this event.
- `static transports`: the transport factories the entry supports, keyed by transport kind. `NexusClient` (core and
  browser) has `webSocket`; `NodeNexusClient` adds `httpSocket`, `tcp`, `tcpTls` and `uds`.

`ClientConfig` is a plain object: exactly one transport property (`webSocket`, `httpSocket`, `tcp`, `tcpTls`, `uds`)
plus the common options, named as the C# `ClientConfig`/`ConfigBase` properties in camelCase, durations in
milliseconds. `authenticate({signal})` may return the token synchronously or as a promise. It runs before the transport
connects, on every connect and reconnect, bounded by the handshake timeout; if it throws, the connect fails with
`DisconnectReason.Authentication` and no connection is opened (F05 semantics).

The optional `clock` property (C# `ConfigBase.Time`) is the time source of every client and session timer: the
connection, handshake and authenticate timeouts, the keepalive, the idle and progress timeouts and the disconnect delay.
It is a `Clock` (`src/session/clock.js`): `now()` in milliseconds on a monotonic clock, plus `setTimeout`,
`clearTimeout`, `setInterval` and `clearInterval` with opaque handles. The default, `systemClock`, is
`performance.now()` and the global timers. Tests inject a manual clock (`test/session/support/ManualClock.js`).

**Keepalive.** Only the client pings (spec §5.5). The interval is `pingInterval` when set, else a third of the
server's advertised `idleTimeoutMs` (S04), else 10,000 ms. Each tick first checks the receive timeout (`timeout`) and
the progress timeout (`progressTimeout`, §9), then sends a `Ping`. In a browser, with the default clock, the keepalive
runs in a dedicated Worker created from a `blob:` URL, because main-thread timers in background tabs are throttled to
about once a minute or frozen, which would let the server's idle timeout expire. If no Worker can be created (no
`Worker`, or a Content-Security-Policy without `worker-src blob:`) it falls back to `setInterval`. Node has no global
`Worker` and always uses `setInterval`; an injected clock always uses `clock.setInterval`.

`isSecure` is true only for transports that declare themselves secure: TLS, WSS, HTTPS and (Node) UDS. Over any other
transport a client logs `NexNet: unencrypted transport (<transport>); credentials and data are visible on the network.`
once per instance, on the first connect (D7).

The `Transport` interface between the session and the transports: `write(chunks)`, `onData(cb)`, `onClose(cb)`,
`close(reason)`, `isSecure`, `remoteCertificate`, `name`. A `TransportFactory` is `(config, {signal}) => Promise<Transport>`.
The Node TLS transports (`tcpTls`, `https:` HttpSocket, `wss:` WebSocket) set `remoteCertificate` to the server's
`X509Certificate` (`node:crypto`); every other transport, the browser WebSocket included, sets it to null. The session
exposes it as `session.remoteCertificate`; `NexusClient` has no public accessor for it yet.

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
- `onInvocationException(error, methodId)` (C# `OnInvocationException`): called when a method that returns a result
  throws, before the failure is sent. Return a `NexusRemoteError` (`{ code?: number | null, message?: string | null }`)
  to send that in place of the exception's detail, whatever `remoteExceptionDetail` says; return null or undefined to
  send what that setting selects (type and message for `TypeAndMessage`, otherwise nothing). A `code` that is not an
  int32 is sent as null; the remote type is never sent with a returned error. Not called for canceled or
  fire-and-forget invocations. A throw from the hook is logged and treated as null.
- `_separatePool(methodId)` (protected): true for `[NexusMethod(SeparatePool = true)]` methods. Such invocations run in
  a second execution pool limited by `maxSeparatePoolInvocations`; all others share the pool limited by
  `maxConcurrentConnectionInvocations`. The base returns false; generated code overrides it only when the nexus has
  such methods.

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
`cause` is set as an own, non-enumerable property, because not every platform's `DOMException` constructor accepts a
`cause` option; read it as `error.cause`. When the session ends, server-to-client invocations that are queued are
dropped and those running see `ctx.signal` abort.

`ConnectionState`, `ConnectionResultState`, `DisconnectReason` (wire codes 20–38), `RemoteExceptionDetail`,
`NexusCollectionState` and `DateTimeKind` are frozen objects with the C# values.

### 3.7 Transport errors and TLS

A transport factory rejects a failed connect with a `NexusTransportError` carrying two extra fields
(`TransportConnectError` in `src/transport/TransportBase.js`):

- `transportError`: the C# `TransportError` name (protocol spec §13.1): `InternalError`, `ConnectionAborted`,
  `AddressInUse`, `InvalidAddress`, `ConnectionTimeout`, `Unreachable`, `ConnectionRefused`, `ProtocolError`,
  `ConnectionReset` or `AuthenticationError`;
- `status`: the HTTP status of a refused upgrade (HttpSocket and Node WebSocket only); absent otherwise.

`reason` is `DisconnectReason.Authentication` for `AuthenticationError` and `SocketError` for everything else, as in
the C# client. `connect()` rethrows this error as is; `tryConnect()` returns it as `error` with that
`disconnectReason`. A connect that exceeds `connectionTimeout` fails with `DisconnectReason.Timeout` instead.

| Failure | `transportError` | `reason` |
|---|---|---|
| Node socket error `ECONNREFUSED`, `ENOENT` (UDS path) | `ConnectionRefused` | `SocketError` |
| `ENOTFOUND`, `EAI_AGAIN`, `ENETUNREACH`, `EHOSTUNREACH` | `Unreachable` | `SocketError` |
| `ECONNRESET`, `EPIPE` | `ConnectionReset` | `SocketError` |
| `ECONNABORTED` | `ConnectionAborted` | `SocketError` |
| `ETIMEDOUT` | `ConnectionTimeout` | `SocketError` |
| `EADDRINUSE` | `AddressInUse` | `SocketError` |
| `EADDRNOTAVAIL`, `EINVAL` | `InvalidAddress` | `SocketError` |
| Any error after the TCP connect and before the TLS handshake completes, or a TLS/certificate error code | `AuthenticationError` | `Authentication` |
| Upgrade refused with `401` or `403` (HttpSocket, Node WebSocket) | `AuthenticationError`, `status` set | `Authentication` |
| HttpSocket upgrade refused with any other status, `2xx` included | `InternalError`, `status` set | `SocketError` |
| Node WebSocket upgrade refused with `5xx` | `InternalError`, `status` set | `SocketError` |
| Node WebSocket upgrade refused with any other status | `ProtocolError`, `status` set | `SocketError` |
| `101` with a wrong `Upgrade`/`Connection`/`Sec-WebSocket-Accept`, or a subprotocol or extension selected | `ProtocolError` | `SocketError` |
| Browser WebSocket: any connect failure (the platform hides the cause and the status) | `InternalError` | `SocketError` |
| Any other error | `InternalError` | `SocketError` |

After the connection is up, the error a transport ends with is only logged (at `debug`): the session ends with
`DisconnectReason.SocketError`, or `SocketClosedWhenWriting` when a write failed. The TCP, TLS, UDS and HttpSocket
transports report the Node socket error, or none for a plain end. The WebSocket transports report `ConnectionAborted`
for a close other than code 1000 (or a connection lost without a Close frame, 1006) and `ProtocolError` for an RFC 6455
violation or a text message. A write on an ended transport rejects with `ConnectionAborted`.

**Credentials.** The greeting token (`authenticate`) is the canonical credential for every transport (D15). For the
HTTP-based transports in Node, extra upgrade headers, an `Authorization` header for example, go in
`config.webSocket.headers` or `config.httpSocket.headers`; the transport sets its own upgrade headers (`Connection`,
`Upgrade` and, for WebSocket, `Sec-WebSocket-Version` and `Sec-WebSocket-Key`) after them. Browsers
cannot send custom headers on a WebSocket, so a token for a NexNet.Asp query-string hook (F06) goes in the URL
(`wss://host/nexus?token=…`), in Node as well as in browsers.

**TLS options and pinning (Node).** `TlsOptions` follow `tls.connect`. The host name is always verified with
`tls.checkServerIdentity` (against `servername`, defaulting to the URL or `host` name). A user
`checkServerIdentity(hostname, certificate)` runs after that check passes; returning an `Error` fails the connect with
`AuthenticationError`. It is the pinning hook, and it supports both pin forms Java accepts, because `certificate` is
Node's detailed peer certificate:

- certificate SHA-256: `certificate.fingerprint256` (colon-separated upper-case hex), or the SHA-256 of
  `certificate.raw` (DER);
- SPKI SHA-256: the SHA-256 of `certificate.pubkey`, which is the DER `SubjectPublicKeyInfo` (the HPKP
  `pin-sha256` value when base64-encoded);
- the issuers are reachable through `certificate.issuerCertificate`, to pin an intermediate or the root.

```js
import { createHash } from 'node:crypto';
const pin = 'MZEa+vKvxw+5EI8S4C6W811NN39NrGjkKDakkDBwHdE='; // base64 SHA-256 of the server's SPKI
const tls = {
  ca: testCaPem,
  checkServerIdentity: (host, cert) =>
    createHash('sha256').update(cert.pubkey).digest('base64') === pin ? undefined : new Error('certificate pin mismatch'),
};
```

Node calls the hook only when chain validation succeeded. With `rejectUnauthorized: false` and a certificate that does
not chain to a trusted CA, it is **not called** and the connect succeeds, so pinning can only narrow CA trust, never
replace it: pass the issuing CA in `ca` to pin a self-signed or private-CA server. The runtime has no built-in pin list
yet. Browsers do the TLS themselves and ignore `tls`: no custom CA, client certificate or pinning (capability skips).

### 3.8 Runtime internals: session extension points

The layers above the session core (invocations J07, pipes J08, collections J09, reconnection J10) plug into
`NexusSession` (`src/session/NexusSession.js`) through these members. They are internal to the runtime: not exported
from the package entries, and generated code never uses them. The client creates one session per connect attempt and
attaches the layers (`#attachSessionLayers`) **before** `start()`, so frames that arrive in the same read as the
`ServerGreeting` find their handlers.

| Member | Contract |
|---|---|
| `setHandler(type, handler)` | Registers the `FrameHandler` of a message type received after the handshake. A received type without a handler is a protocol error (`ProtocolError`). `Ping`, `ServerGreeting`, `ClientGreeting` and `ClientGreetingReconnection` belong to the session: registering them throws `TypeError`. A later registration for the same type replaces the earlier one |
| `send(frame)` | Queues one encoded frame. Resolves true once the transport took it, false when the session ended first or the handshake has not completed (nothing is sent then). Frames queued in the same tick go out in one transport write |
| `controlSender` | The `ControlSender` (C# `SessionControlSender`). Its requests (`requestPong`, `requestPipeControl(source)`, `requestAbort(id, kind, reason)`, `requestInvocationCancellation(id)`, `returnInvocationCredit(n)`) only record what is owed; frames are built when the write queue flushes, so repeated requests cost at most one frame each, and credit is batched to half the advertised `invocationCredits`. A `PipeControlSource` implements `takePendingControl(): Uint8Array[]`. Everything pending is dropped when the session ends |
| `onClose(listener)` | `listener(reason)` runs once, synchronously, when the session reaches `Disconnected`, after the transport closed and before `closed` resolves. A throwing listener is logged; the others still run |
| `addProgressProbe(probe)` | `probe(now, progressTimeout)` runs on every keepalive tick (§9) after the session's own write-stall check. Return a description of what stalled, or null; the first non-null result ends the session with `DisconnectReason.Stalled` |

A `FrameHandler` receives a `Frame` (`type`, `body`, and the post-header fields `pipeId`, `invocationId`, `kind`) and
returns a disconnect reason to end the session (the disconnect byte is sent) or nothing / 0 to continue. A throw ends
the session with `ProtocolError`. Handlers run synchronously in the transport's data callback and must not call user
code from there; defer it to a microtask.

**Body lifetime.** `frame.body` is a view into the frame reader's buffer, which is reused and compacted by later
reads. It is valid only until the handler returns: copy (`body.slice()`) anything that must outlive the call, such as
a pipe payload queued for a reader or a large-message chunk.

Other session members the layers read: `ready` and `closed` (promises that never reject), `id`,
`isHandshakeComplete`, `isDisconnecting`, `disconnectReason`, `options` (the resolved `ClientConfig`),
`peerMaxMessageSize`, `peerInvocationCredits`, `sendMaxMessageSize`, `idleTimeoutMs`, `pingInterval` and the
frame/byte counters used by `getSnapshot()`.

**Invocation codec.** The invocation layer gets its writers and readers from a process-wide factory in
`src/invocation/codec.js`: `invocationCodec()` returns `{ createWriter(), createReader(bytes, serializerOptions) }`,
by default `new MsgPackWriter()` and `new MsgPackReader(bytes, options)`. `DispatchContext.writeResult` and the reader
passed to `_dispatch` come from it. `setInvocationCodec(codec)` replaces it (null restores the default) and returns the
previous one; only tests use it (until J02 lands they install `test/invocation/support/testCodec.js`). Generated code
never does.

### 3.9 Diagnostic counters

Two process-wide counter sets support the leak audit (H05): after every client has disconnected, every counter must be
zero. Each getter returns a snapshot copy. Both live in runtime modules that the package `exports` map does not expose
yet (`src/session/diagnostics.js`, `src/invocation/diagnostics.js`); tests import them by path.

| Function | Counter | Counts |
|---|---|---|
| `getSessionCounters()` | `openSessions` | Sessions created and not yet `Disconnected` |
| | `timers` | Session timers running: handshake timeout, keepalive, disconnect delay, disconnect-write timeout |
| | `pendingWrites` | Frames queued or being written and not yet flushed |
| `getInvocationCounters()` | `pendingInvocations` | Outbound calls not yet completed (waiting for credit, sending, or waiting for their result) |
| | `reservedInvocationIds` | Outbound invocation IDs reserved (awaiting a result, or a late result after a cancel) |
| | `creditWaiters` | Outbound calls parked waiting for invocation credit |
| | `inboundInvocations` | Server-to-client invocations queued or running |
| | `largeMessages` | Large messages being sent or received |

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
