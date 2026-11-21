# Interop control protocol (version 1)

**Stable contract**: a change must update every consumer (the C# runner in
`src/NexNet.Interop/Remote` and `Hosts`, the Node and browser hosts in `clients/js/packages/interop-host-*`, the JVM
host in `clients/java/interop-host`, future hosts) in one PR.

The cross-language test harness runs every client test (written in C#, TUnit) against clients written in other
languages. Each language has a thin, long-lived **language host**: a process that owns real NexNet clients of its
language and drives them on request. The C# test process (the **runner**) talks to each host over one **control
connection** with the protocol below. The protocol is independent of NexNet itself: it never carries NexNet frames,
only commands and their results.

Words in capitals (MUST, SHOULD, MAY) are used as in RFC 2119.

## 1. Roles and lifecycle

```
runner (C#, TUnit)                                   language host (node / java / browser …)
  listen 127.0.0.1:0
  launch host --control 127.0.0.1:<port> --token <t> ──► dials in
                                   ◄──────────────── hello{proto, token, lang, …}
  checks token, proto, schemaHash
  req{id, test, op, args, timeoutMs} ──────────────►
                                   ◄──────────────── res{id, ok, value | error, hostNs}
                                   ◄──────────────── evt{test, h, ev, data, hostNs}
  req{op:"host.shutdown"} ─────────────────────────►
                                   ◄──────────────── res, bye, exit 0
```

- **Requests** (`req`) flow only from the runner to the host. Each gets exactly one **response** (`res`).
- **Events** (`evt`) flow only from the host to the runner and are never answered. A runner answers a host's need for
  data (a forwarded script call, §7.3) with a separate request (`callback.reply`).
- One connection serves one host process for its whole life. When it closes, the host exits; when the host exits, the
  runner treats the host as crashed (§9).

## 2. Carrier

1. The runner listens on `127.0.0.1:0` (TCP, IPv4 loopback, ephemeral port).
2. It starts the host with the arguments `--control 127.0.0.1:<port> --token <token>` appended to the launch arguments
   (§10). A browser host also gets `--browser chromium|firefox|webkit`. `<token>` is 64 lower-case hex characters
   (32 random bytes), new for every launch.
3. The host connects to that address and sends `hello` (§4.1) as its first frame within the startup timeout (default
   60 s).
4. The runner accepts one connection per launch. A connection whose first frame is not a valid `hello` with the right
   token is refused: the runner sends `bye` with the reason, closes it and fails the launch.

**Browser hosts.** A browser cannot open TCP connections. The Node launcher of the browser host listens on a
loopback WebSocket, dials the runner over TCP, and bridges the two byte streams unchanged: each WebSocket binary message
carries any number of bytes of the control stream (a frame MAY span messages, a message MAY hold several frames). The
browser page runs the host and the launcher only relays bytes; the protocol is exactly the same.

## 3. Framing

Every message is one frame:

| Bytes | Field | Meaning |
|---|---|---|
| 4 | `length` | Unsigned 32-bit **big-endian**: the number of bytes that follow (kind + payload). |
| 1 | `kind` | `0x01`: the payload is one UTF-8 JSON object (no BOM). `0x02`: reserved for binary payloads. |
| length − 1 | payload | |

- `length` MUST be at least 1 and at most **64 MiB** (67 108 864). A receiver that reads a larger or zero length, an
  unknown kind, kind `0x02` (reserved in version 1) or a payload that is not a JSON object fails the connection: it
  sends `bye` if it can and closes.
- The end of the stream between frames is a clean close. The end of the stream inside a frame is an error.
- Senders MUST NOT interleave frames: each frame is written whole before the next one starts.

## 4. Messages

Every payload is a JSON object with a string member `t`, the message type. Receivers MUST ignore members they do not
know (so a later minor addition is not a breaking change) and MUST fail the connection on an unknown `t`.

### 4.1 hello (host → runner, first frame)

```json
{"t":"hello","proto":1,"token":"<token>","lang":"node","runtime":"node 22.11.0","lib":"@dtronix/nexnet 0.20.0",
 "schemaHash":"<sha256 hex>","capabilities":["Tcp","Uds","HttpSocket","CustomCa"],
 "transports":["Tcp","TcpTls","Uds","HttpSocket","HttpSocketTls","WebSocket","WebSocketTls"],
 "maxConcurrency":8,"pid":12345}
```

| Member | Type | Meaning |
|---|---|---|
| `proto` | int | Protocol version: `1`. The runner refuses any other value. |
| `token` | string | The `--token` argument, unchanged. |
| `lang` | string | The language name, as in `NEXNET_TEST_LANGS` (`node`, `java`, `browser-chromium`, …). |
| `runtime` | string | Free text: runtime name and version (`node 22.11.0`, `OpenJDK 21.0.5`, `Chrome 131`). |
| `lib` | string | Free text: the NexNet client library and its version. |
| `schemaHash` | string | Lower-case hex SHA-256 of the bytes of the `nexnet-ir.json` the host's generated code was built from (§4.6). |
| `capabilities` | string[] | Names of the `ClientCapabilities` flags the client supports (`AuthorizationHeader`, `ClientCertificate`, `Pinning`, `CustomCa`, `RemoteCertificate`, `HttpSocket`, `Tcp`, `Uds`, `Quic`, `FakeTime`, `ClientPool`, `PipeInputCompletion`, `ChannelReadCancellation`). Unknown names are ignored. |
| `transports` | string[] | Names of the `ClientTransport` values the client implements (`Tcp`, `TcpTls`, `Uds`, `HttpSocket`, `HttpSocketTls`, `WebSocket`, `WebSocketTls`). `Quic` is a C#-only transport: only the in-process C# driver runs it, and no language host lists it. |
| `maxConcurrency` | int ≥ 1 | How many tests the host can run at once. The runner never runs more tests at once on this host. |
| `pid` | int? | The process id of the host (for thread dumps); absent in a browser. |

The runner then registers the language's transports and capabilities (`ClientLanguages.Register`) and runs a warm-up
self-test: `ping`, `test.begin` and `test.end` of the test id `__warmup` (the report MUST be clean).

### 4.2 req (runner → host)

```json
{"t":"req","id":17,"test":"<test id>","op":"invoke","args":{…},"timeoutMs":30000}
```

| Member | Type | Meaning |
|---|---|---|
| `id` | int | Unique per connection, increasing. Echoed in the response. |
| `test` | string? | The test scope (§5). Absent or null for host-level ops (`ping`, `host.*`, `log.level`). |
| `op` | string | The operation (§6). |
| `args` | object? | The operation's arguments; absent means `{}`. |
| `timeoutMs` | int? | Bound on the operation's duration (the runner itself waits that long plus a 5 s grace for the response). Absent or 0: no bound (the op runs until it completes or the scope ends). When the bound passes, the host fails the request with kind `Timeout` and cancels what the op waits for (not the client operation itself unless the op says so). |

### 4.3 res (host → runner)

```json
{"t":"res","id":17,"ok":true,"value":{"result":"hi"},"hostNs":123456789}
{"t":"res","id":18,"ok":false,"error":{"kind":"RemoteInvocation","message":"boom","detail":{"type":"System.InvalidOperationException"}},"hostNs":123459999}
```

| Member | Type | Meaning |
|---|---|---|
| `id` | int | The request id. |
| `ok` | bool | True: `value` holds the result. False: `error` holds the failure. |
| `value` | object? | The op's result object (§6). Absent means `{}`. |
| `error` | object | `{kind, message, detail?}` (§8). |
| `hostNs` | int | The host's monotonic clock in nanoseconds when it sent the response (for transcripts; never compared across hosts). |

### 4.4 evt (host → runner)

```json
{"t":"evt","test":"<test id>","h":"c1","ev":"script.call","data":{"slot":"ClientEchoString","args":["x"]},"hostNs":123}
```

| Member | Type | Meaning |
|---|---|---|
| `test` | string? | The test scope of the handle, or null for host-level events. |
| `h` | string? | The client handle the event is about, or null. |
| `ev` | string | The event name (§7). |
| `data` | object? | Event data. |
| `hostNs` | int | As in `res`. |

### 4.5 bye (either direction, last frame)

`{"t":"bye","reason":"…"}`. Sent right before a deliberate close (refused hello, `host.shutdown`, protocol error).
Optional: a close without `bye` is still a close.

### 4.6 schemaHash

The value model of the tests is the generator IR of the shared test nexuses (`src/NexNet.TestNexus`): the runner
computes the SHA-256 of the bytes of `src/NexNet.TestNexus/obj/nexnet-ir.json` (written by the NexNet generator's
unpack target on every build; `NEXNET_INTEROP_IR` overrides the path). The host reports the hash of the IR file its
generated test scaffolding was produced from; build tools embed it in the host at build time.

A different hash means the host's generated code is stale. The runner fails the launch (every row of the language
fails) with the message:

```
The <lang> language host was built from TestNexus IR <host hash>, but the current IR is <expected hash>.
Rebuild it: <build command>
```

## 5. Test scopes and handles

- Every test that uses a host has a **test id**: an opaque string chosen by the runner (the TUnit test context id).
- `test.begin{}` (with `req.test` set) opens the scope. Every request that names a scope that is not open fails
  with kind `BadRequest`.
- **Handles** are strings allocated by the host (`c1`, `p7`, `f3`, …; their form is the host's choice, unique per
  connection). Cancellation tokens are the one exception: the runner names them (§6.5), so it can create a token and
  use it in the next request without waiting for a response. A handle or token belongs to the scope that created it. A request that uses a handle of another scope,
  an unknown handle or a released handle fails with kind `UnknownHandle`.
- `test.end{}` closes the scope: it returns the **leak report** of the scope (§5.1), then releases everything the scope
  still holds (disposes clients and pipes, cancels timers, tokens and pending waits, completes futures with kind
  `Canceled`). Requests of the scope still running end with kind `Canceled`.
- A host runs several scopes at once (up to `maxConcurrency`); scopes never share clients.

### 5.1 Leak report

```json
{"openClients":0,"openPipes":1,"timers":0,"pendingInvocations":0,"subscriptions":0,"details":["pipe p3 of c1 neither completed nor disposed"]}
```

| Member | Counts |
|---|---|
| `openClients` | Clients that are neither disposed nor in state `Disconnected`. |
| `openPipes` | Pipes that are neither complete in both directions nor disposed. |
| `timers` | Timers of the client runtime still scheduled for clients of the scope (ping, reconnect, `afterMs` cancellations, delays of scripts). A disconnected client MUST have none. |
| `pendingInvocations` | Invocations started and not completed (a fire-and-forget invocation until it is sent). |
| `subscriptions` | Listeners the host attached for the scope and did not detach: collection watches (§6.10) not ended by `list.unwatch`. A collection that is still enabled is not a subscription: it ends with its client (`openClients` counts a client that is still connected). |
| `details` | One human-readable line per leak. |

Before counting, a host waits up to 1 s for pending invocations to settle. Any non-zero count fails the test.

## 6. Operations

Conventions: `h` is a client handle, `pipe` a pipe handle. Results are objects; `{}` means an empty result. Every op
not listed under "host-level" requires `req.test`. Unless an op says otherwise, it completes once its effect is done.

### 6.1 Ordering

A host reads requests in order and MUST **start** them in arrival order. Before it starts the next request it MUST have
applied the synchronous effect of each of these ops: `test.begin`, `client.create`, `script.set`, `script.clear`,
`signal`, `token.create`, `token.cancel`, `callback.reply`, `pipe.create`, `list.op` (the change is handed to the
client runtime), `list.watch` and `list.unwatch` (the listener is attached or detached), and `invoke` (the invocation is handed to the client runtime: its message queued for sending, in
order). Other ops MAY run concurrently. So a script set before an invocation applies to the calls it causes, and
fire-and-forget invocations are sent in request order.

Responses MAY arrive in any order. Events of one client arrive in the order the host raised them, and an event raised
before a response is sent arrives before that response.

### 6.2 Host-level (no test scope)

| Op | Args | Result | Notes |
|---|---|---|---|
| `ping` | | `{}` | MUST be answered within 1 s even while other ops run (§9). |
| `host.dumpThreads` | | `{text}` | A dump of every thread/task the host can list (Java: every thread's stack; Node: `process.report.getReport()` as JSON text; browser: pending tasks it tracks). |
| `host.shutdown` | | `{}` | Responds, sends `bye`, closes, exits with code 0. |
| `log.level` | `{level}` | `{}` | Host log level: a `NexusLogLevel` name (`Trace`, `Debug`, `Information`, `Warning`, `Error`, `Critical`, `None`). Host logs travel as `log` events. |

### 6.3 Test scope

| Op | Args | Result |
|---|---|---|
| `test.begin` | | `{}` |
| `test.audit` | `{h?}` | The leak report (§5.1) of the scope, or of client `h` and what it owns, without releasing anything. |
| `test.end` | | The leak report of the scope; then releases it (§5). |

### 6.4 Clients

| Op | Args | Result |
|---|---|---|
| `client.create` | `{spec, nexus:{client, server}}` | `{h}`. Creates a client, not connected. `nexus.client`/`nexus.server` are the full metadata names of the client and server nexus interfaces (`NexNet.TestNexus.Interop.IInteropClientNexus`); a host that has no generated code for the pair fails with `NotSupported`. `spec` is a `ClientSpec` (§6.4.1). An option the client cannot honor fails with `NotSupported`, never silently. |
| `client.connect` | `{h}` | `{}`; fails with the connection error (kind `Transport`, …). |
| `client.tryConnect` | `{h}` | `{ok}` |
| `client.disconnect` | `{h}` | `{}`, once the client is `Disconnected`. |
| `client.dispose` | `{h}` | `{}`; releases the handle and everything the client owns. Disposing twice is not an error. |
| `client.state` | `{h}` | `{state}`: a `ConnectionState` name (`Disconnected`, `Connecting`, `Connected`, `Reconnecting`, `Disconnecting`). |
| `client.remoteCertificate` | `{h}` | `{der}`: the certificate the server presented on the client's current connection, as base64 DER; `null` when the client is not `Connected` or its transport has no TLS. A host without the `RemoteCertificate` capability (the browser) answers `null`. Client handles only. |
| `client.awaitState` | `{h, state}` | `{}` once the state is reached (at once if it is current). Bounded by `timeoutMs`. |
| `client.awaitReady` | `{h}` | `{}` once connected and ready for invocations. Bounded by `timeoutMs`. |
| `client.awaitDisconnected` | `{h}` | `{reason}`: a `DisconnectReason` name, once the current session is disconnected (at once if it already is). Bounded by `timeoutMs`. |
| `time.advance` | `{h, ms}` | `{}`. Moves the test clock of a client created with `spec.fakeTime`; `InvalidOperation` otherwise. |

#### 6.4.1 ClientSpec JSON

The members of `NexNet.Interop.ClientSpec`, camelCase; null members are omitted (the client's default):

| Member | Type | |
|---|---|---|
| `transport` | string | `ClientTransport` name. Always set by the runner. |
| `address` | string | `tcp://host:port`, `tls://host:port`, `uds:<path>`, `http(s)://…`, `ws(s)://…`. |
| `tlsServerName`, `caPem`, `clientCertificatePem`, `clientKeyPem`, `pinSha256` | string | TLS options. |
| `insecureSkipVerify` | bool | |
| `connectionTimeoutMs`, `timeoutMs`, `handshakeTimeoutMs`, `pingIntervalMs`, `maxMessageSize`, `invocationCredits` | int | |
| `maxConcurrentConnectionInvocations` | int | How many invocations from the server the client runs at once (1 – 1000); further ones queue. |
| `remoteExceptionDetail` | string | `RemoteExceptionDetail` name (`None`, `TypeAndMessage`): what the client sends the server when a client nexus method fails. |
| `onInvocationException` | object | `{code?, message?}`: the client nexus's `OnInvocationException` hook returns this application error (code int, message string; an absent member is null) for every failed client nexus method, whatever `remoteExceptionDetail` says. Absent: no hook. A pool (§6.11) refuses it with `NotSupported`. |
| `reconnectDelaysMs` | int[] | Null: no reconnection. |
| `reconnectContinuously` | bool | |
| `authToken` | string | Greeting token, UTF-8 text. |
| `authorizationHeader` | string | |
| `fakeTime` | bool | |
| `logLevel` | string | `NexusLogLevel` name; null disables client logging. Client logs travel as `log` events. |

Unknown members MUST be refused with `NotSupported` (a newer runner asks for something the host cannot do).

### 6.5 Invocations

Method references are objects `{nexus, name, id}`: the full metadata name of the server nexus interface, the method
name and its wire id (the host MAY use either the name or the id; they always agree).

| Op | Args | Result |
|---|---|---|
| `invoke` | `{h, method, args, pipe?, cancel?, detach?}` | Without `detach`: `{result}` once the call completes; `result` is the typed-JSON value (null for methods without a value). With `detach: true`: `{future}` as soon as the invocation is handed to the client runtime (fire-and-forget methods are always invoked this way). |
| `future.await` | `{future}` | `{result}` or the call's error; releases the future. Bounded by `timeoutMs`. |
| `future.poll` | `{future}` | `{done:false}`, or `{done:true, ok:true, result}` / `{done:true, ok:false, error}`; a done future is released. |
| `token.create` | `{token}` | `{}`: a new cancellation source named `token` (a string chosen by the runner, unique in the scope; the C# runner uses `r1`, `r2`, …). |
| `token.cancel` | `{token}` | `{}`; cancels it. Canceling an unknown or released token is not an error. |
| `token.dispose` | `{token}` | `{}`; releases it. |

- `args` is the array of the serialized arguments in wire order, each in typed JSON (golden format, `tests/golden/README.md`).
- `pipe` is the pipe handle passed for the method's `INexusDuplexPipe` or `INexusDuplexChannel<T>` parameter. A pipe of
  another client of the scope is handed to the runtime as is: the runtime refuses it (kind `InvalidOperation`), which is
  how a test passes a pipe of the wrong session.
- `cancel` is `{afterMs}` (a host-side timer cancels the call that many milliseconds after it is handed to the runtime,
  so the race under test does not include the control connection) or `{token}` (the call observes that token). A call
  whose token is canceled before it is sent fails with kind `Canceled` and is not sent (C# `TaskCanceledException`, Java `CancellationException`, JavaScript `AbortError`; never a default value).
- Errors use the kinds of §8: the server's exception is `RemoteInvocation`, a refused authorization `Unauthorized`, a
  canceled call `Canceled`, a disconnected session `Disconnected`.
- A future of a fire-and-forget method completes when the invocation is sent. A future that is never awaited is released
  at `test.end` and is not a leak once complete.

### 6.6 Scripts (the client nexus)

Every method of the client nexus and the two lifecycle events `OnConnected` / `OnDisconnected` is a **slot**,
identified by its name. The host decides what the client does when the server calls a slot from the slot's script.

| Op | Args | Result |
|---|---|---|
| `script.set` | `{h, slot, steps, times?}` | `{}`. Replaces the slot's script; `times` (int ≥ 1, or null for every call) is how many calls the steps apply to. |
| `script.clear` | `{h, slot}` | `{}`. Removes the steps and the limit. |
| `signal` | `{h, name}` | `{}`. Raises a signal of client `h`; a raised signal stays raised. |
| `callback.reply` | `{h, callId, result?, error?}` | `{}`. Completes a forwarded call (§7.3) with the typed-JSON `result`, or fails it with `error` `{kind, message}`. A reply for a call that is already over is ignored. |

**Steps** (`steps` is an array of objects with a string member `step`):

| Step | Members | Effect |
|---|---|---|
| `return` | `value` | Completes the call with the typed-JSON `value` (null for methods without a result). |
| `throw` | `kind`, `message` | Fails the call with the native exception of the `ErrorKind`; the server sees it as a remote exception. `Disconnected` always uses the message `Session is not connected`. |
| `delay` | `ms` | Waits; fails the call as canceled if the call is canceled meanwhile. |
| `awaitSignal` | `name` | Waits until the signal is raised; fails as canceled on cancellation. |
| `awaitCancel` | | Waits until the call's cancellation token is canceled, then continues. |
| `invoke` | `method`, `args`, `returnKind` | Invokes a server method (`returnKind`: `Void`, `Task`, `TaskOf`) from inside the handler and waits for it (a `Void` method is only sent); its error fails the call. |
| `named` | `handler`, `options?` | Runs a built-in handler (§6.9). |
| `forward` | | Forwards the call to the runner (§7.3) and completes it with the runner's reply. |

**Semantics** (the reference is `ScriptInterpreter` in `src/NexNet.Interop/Scripting`; hosts MUST match it exactly):

1. Each call is reported first: the host sends the `script.call` event (§7.1) before any step runs.
2. When the slot has a `times` limit, its steps apply to that many calls counted from the last `script.set` /
   `script.clear` of the slot; later calls run no steps.
3. Steps run in order. `return`, `forward` and `throw` end the call. The other steps continue to the next step.
4. A call whose steps run out without ending it completes with the default result: typed JSON `null`.
5. An unscripted slot (the initial state) therefore reports the call and returns `null`.
6. A fire-and-forget slot runs the same way; its result is dropped and its errors are not reported.

### 6.7 Pipes

| Op | Args | Result |
|---|---|---|
| `pipe.create` | `{h}` | `{pipe}`: a duplex pipe of client `h`, to pass to `invoke`. |
| `pipe.ready` | `{pipe}` | `{}` once both sides have the pipe. Bounded by `timeoutMs`. |
| `pipe.write` | `{pipe, b64}` or `{pipe, pattern:{seed, length}}` | `{}` once written (flushed into the pipe, subject to its flow control). `b64`: base64 bytes. `pattern`: `length` bytes of the deterministic pattern: byte `i` is `(i * 31 + seed) mod 256` (`PipePattern`). Fails with `InvalidOperation` if the pipe completes first. |
| `pipe.read` | `{pipe, min, max}` | `{b64}`: at least `min` (≥ 0) and at most `max` (≥ 1) bytes; fewer, possibly none, when the other side completes. |
| `pipe.drain` | `{pipe}` | `{length, sha256}`: reads until the other side completes; lower-case hex SHA-256 of everything read. |
| `pipe.tryWrite` | `{pipe, b64}` | `{ok}`: writes the bytes as `pipe.write` does, but a pipe whose writing side is complete (the other side completed its reading side or the whole pipe, or the session ended) is a result, not an error: `ok` is false and the bytes were not all delivered (C# `FlushResult.IsCompleted`). |
| `pipe.complete` | `{pipe}` | `{}`; completes the writing side. |
| `pipe.completeInput` | `{pipe}` | `{}`; completes the reading side only (C# `Input.CompleteAsync()`): the other side's writes see the pipe complete, and this side can still write. Capability `PipeInputCompletion`; a host without it fails with `NotSupported`. |
| `pipe.whenComplete` | `{pipe}` | `{}` once the pipe is complete in both directions. Bounded by `timeoutMs`. |
| `pipe.dispose` | `{pipe}` | `{}`; releases the pipe. Disposing twice is not an error. A held pipe (`pipe.accept`) is not disposed: its `pipe.hold` step ends, and the client runtime completes the pipe when its method returns. |
| `pipe.accept` | `{h, name}` | `{pipe}`: the next pipe a `pipe.hold` step (§6.9) of client `h` published under `name`, waiting until one is (bounded by `timeoutMs`). The handle takes every pipe and channel op above, so the test drives a pipe the server opened like one of its own. Each published pipe is accepted once, in publication order. |

Reading grants the other side pipe credit (never the arrival of bytes; context §6).

### 6.8 Channels

A channel is a view of a pipe. `type` is the item type as a golden type expression (`tests/golden/README.md` §Types),
with declared types by full metadata name, for example `long`, `string`, `NexNet.TestNexus.Interop.InteropItem`,
`List<int>`. Items are typed JSON.

| Op | Args | Result |
|---|---|---|
| `channel.writer` | `{pipe, type}` | `{}`. Uses the pipe as the writer of a channel of `type` (waits until the pipe is ready). |
| `channel.write` | `{pipe, items}` | `{ok}`: false when the channel is complete. Several items are written as one batch. |
| `channel.complete` | `{pipe}` | `{}`; completes the channel writer. |
| `channel.reader` | `{pipe, type}` | `{}`. Uses the pipe as the reader of a channel of `type`. |
| `channel.read` | `{pipe, max, cancel?}` | `{items, complete}`: up to `max` (≥ 1) items, waiting for at least one unless the channel is complete; `complete` is true once the channel is complete and drained (then `items` is empty). `cancel` is `{afterMs}` or `{token}` as in `invoke` (§6.5): a read canceled before an item arrives fails with kind `Canceled` and consumes nothing, so the next read gets the items that arrive later. Capability `ChannelReadCancellation`; a host without it fails a read with `cancel` with `NotSupported`. |
| `channel.readAll` | `{pipe}` | `{items}`: every item until the channel completes. Bounded by `timeoutMs`. |

### 6.9 Named handlers (`named` script steps)

Built into every host; they act on the pipe or channel argument of the call (a method without one fails the call with
`InvalidOperation`).

| Handler | Options | Effect |
|---|---|---|
| `pipe.echo` | | Writes back every byte it reads until the server completes its side, then completes the pipe. |
| `pipe.drain` | | Reads until the server completes its side, then completes the pipe. |
| `pipe.readExact` | `length` | Reads `length` bytes (fewer if the server completes first), then completes the pipe. |
| `pipe.hold` | `name` (string, required) | Publishes the call's pipe to the runner under `name` (`pipe.accept`, §6.7) and waits until the runner disposes that handle (or the client is disposed, or the scope ends); then the call returns. It ignores the call's cancellation, so the runner can still drive the pipe after a disconnect. Does not read, write or complete the pipe. Pipes only: a channel method fails the call with `InvalidOperation`. |
| `channel.echo` | | Writes back every item it reads until the server completes, then completes the channel. |
| `channel.pump` | `count` (int ≥ 0, required), `size` (int ≥ 0, default 0), `delayMs` (int ≥ 0, default 0) | Writes `count` items, one at a time, waiting `delayMs` after each item but the last, then completes the channel. Does not read. Item `i` (0-based) depends on the channel's item type: an integer type (`sbyte` … `ulong`): the number `i` (wrapped to the type's width); `string`: `size` characters, character `j` being `'a' + (i + j) mod 26`; `byte[]`: the first `size` bytes of the pipe pattern of seed `i` (`PipePattern`). Any other item type fails the call with `NotSupported`. |

The C# reference implementation of every handler above is `InProcNamedHandlers` (`src/NexNet.Interop/InProc`), except
`pipe.hold`, which needs the client's state: `InProcClientDriver.HoldAsync`.

### 6.10 Synchronized collections

A collection reference is `{nexus, name, id}` (the server nexus interface, the collection property name, its wire
id). Items are typed JSON.

| Op | Args | Result |
|---|---|---|
| `list.enable` | `{h, collection, cancel?}` | `{ok}`: whether the collection was enabled; when true, it has also received its initial state (ready). `cancel` as in `invoke`. |
| `list.disable` | `{h, collection}` | `{}` |
| `list.op` | `{h, collection, op, item?, index?, from?, to?}` | `{ok}`. `op`: `add` (item), `insert` (index, item), `remove` (item), `removeAt` (index), `replace` (index, item), `move` (from, to), `clear`. |
| `list.snapshot` | `{h, collection}` | `{items}`: the items the client has now. |
| `list.watch` | `{h, collection, watch, throws?}` | `{}`. Attaches a listener to the collection's change events; from the moment it responds, every change event the client raises for the collection is sent as a `list.changed` event (§7.6) carrying `watch`. `watch` is a runner-chosen name, unique per client (so events that arrive before the response can be routed); a name already in use fails with `BadRequest`. Works whether or not the collection is enabled. `throws` (bool, default false): after sending each `list.changed`, the listener throws a native exception out of the runtime's change callback (C# `InvalidOperationException`, Java `IllegalStateException`, JS `Error`), to test that a throwing handler breaks neither the client nor its other listeners. An exception the runtime hands to the platform's uncaught-error path (a JS `EventTarget` listener) is not reported as `host.error` (§7.5). |
| `list.unwatch` | `{h, collection?, watch}` | `{}`. Detaches the listener; no `list.changed` of `watch` follows the response. An unknown watch fails with `UnknownHandle`. A watch not ended by `list.unwatch` counts in `subscriptions` (§5.1). |

A list op with an index out of range (a negative index, for example) fails with the client error kind
`ArgumentOutOfRange` (§8) when the client runtime rejects it before sending.

### 6.11 Client pools (capability `ClientPool`)

A pool is the client pool of the runtime (C# `NexusClientPool`, Java/JS `NexusClientPool`), created in a test scope.
Its clients all use the pool's `spec`. `pool` is a pool handle; a **rental handle** `h` names one rental of a
pooled client.

| Op | Args | Result |
|---|---|---|
| `pool.create` | `{spec, nexus:{client, server}, options}` | `{pool}`: a pool holding no client yet. `spec` and `nexus` as in `client.create` (§6.4); `options` is §6.11.1. |
| `pool.rent` | `{pool, cancel?}` | `{h, client}` on success, `{failed}` when the new client could not connect (below). Bounded by `timeoutMs`. |
| `pool.return` | `{pool, h}` | `{}`: returns the rental; its handle is released. |
| `pool.ensureConnected` | `{h}` | `{ok}`: true when the rented client is connected; otherwise it tries to connect once and `ok` says whether it did. |
| `pool.state` | `{pool}` | `{maxConnections, idle, rented}`: the maximum, the clients idle in the pool, the rentals not returned. |
| `pool.advanceTime` | `{pool, ms}` | `{}`. Moves the test clock of a pool created with `spec.fakeTime` (the health check and every client of the pool share it); timers due run before the response. `InvalidOperation` otherwise. |
| `pool.dispose` | `{pool}` | `{}`: disposes the pool and its idle clients. Rentals not returned stay connected until they are returned (the pool then disposes them). Disposing twice is not an error. |

`test.audit` also takes `{pool}`: the leak report of the pool and its clients.

**Rent** (`pool.rent`):

- It takes the oldest idle client that is connected, disposing the idle clients that are not on the way. With none, it
  creates a client and connects it before it responds. While `maxConnections` rentals are out it waits for a return.
- `client` identifies the pooled client: two rentals of the same client have the same `client` (the pool reused it).
  It is opaque (`pc1`, …). Every rent allocates a new `h`, so a stale rental handle never drives a re-rented client.
- `cancel` is `{token}` (§6.5): the token cancels the wait for a free slot, which fails with kind `Canceled`. A cancel
  during the connection attempt ends it as a connection failure.
- **Connection failure is a result, not an error:** `{failed:{state, reason, message, error?}}`, the client's connection
  result. `state` is a `ConnectionResult.StateValue` name (`Exception`, `Disconnected`, `Canceled`, …), `reason` a
  `DisconnectReason` name (`Authentication` when the server refused the greeting token, `None` when it never answered),
  `error` the error object (§8) of the connection exception, if any. The slot is released; the runner throws
  `ClientPoolConnectionException` with this `ConnectionResult`.
- A disposed pool fails with kind `InvalidOperation` (the runner throws `ObjectDisposedException` without asking).

**Return** (`pool.return`): a connected client goes back to the pool, idle; a client that is not connected, or of a
disposed pool, is disposed.

**Rental handles** accept `client.state`, `client.awaitState`, `client.awaitReady`, `client.awaitDisconnected` and
`invoke` (with `future.*` and `token.*`), as for a client handle. Every other client op (`client.connect`,
`client.tryConnect`, `client.disconnect`, `client.dispose`, `time.advance`, `pipe.*`, `channel.*`, `list.*`,
`script.*`, `signal`) fails with `NotSupported`: the pool owns the connection, and the client nexus of pooled clients
is not scripted (the server's calls to it complete with their default result, and no `script.call` events are sent). A
released rental handle fails with `UnknownHandle`. The runner answers for a returned rental without asking: state
`Disconnected`, `awaitDisconnected` at once with reason `None`, `ensureConnected` false, and invocations throw
`ObjectDisposedException`.

**Leaks.** For `test.audit` and `test.end`, every client of a pool that is not `Disconnected` (idle or rented) counts as
an open client, and a pool not disposed counts one timer (its health check). `test.end` returns every rental, then
disposes every pool of the scope.

#### 6.11.1 Pool options

The members of `NexNet.Interop.ClientPoolSpec`, camelCase; absent members take the runtime default:

| Member | Type | Default | |
|---|---|---|---|
| `maxConnections` | int | 10 | At least 1. Idle and rented clients together. |
| `maxIdleTimeMs` | int | 120000 | At least 0. |
| `minIdleConnections` | int | 1 | At least 0; may exceed `maxConnections`. |

An out-of-range value fails with `BadRequest` (the runner throws `ArgumentOutOfRangeException` without asking). An
unknown member fails with `NotSupported`.

**Health check** (not configurable): every `clamp(maxIdleTimeMs / 4, 100 ms, 30 s)` on the pool's clock, a pass over the
idle clients, oldest first, disposes those that are not connected, keeps the first `minIdleConnections` connected ones,
and disposes the others idle for at least `maxIdleTimeMs`. It never creates clients.

## 7. Events

### 7.1 script.call

`{slot, args}` with `h` set: the server called slot `slot` of client `h` with the typed-JSON arguments `args` (wire
order; `OnConnected`: `[isReconnected]`, `OnDisconnected`: `[]`). Sent for every call before any step runs.

### 7.2 client.state

`{state}` with `h` set: the client changed state. Informational (transcripts); the runner never waits on it.

### 7.3 script.forward / script.forwardCancel

`script.forward{callId, slot, args}`: a `forward` step needs the runner's handler. `callId` is an integer, unique per
host connection. The runner runs the C# handler of the slot and sends `callback.reply` with the same `callId`.
`script.forwardCancel{callId}`: the forwarded call was canceled; the runner cancels the handler's token (it still
replies, and the host ignores the reply).

### 7.4 log

`{level, category?, message}` with `h` set when the line comes from a client, null for the host itself. Kept in the
test's transcript.

### 7.5 host.error

`{message, detail?}`: an error the host could not attribute to a request (an unhandled rejection, a crashed worker).
The runner fails the test of the scope (or the next test, for a host-level error).

### 7.6 list.changed

`{watch, action, index?, oldIndex?, items?, oldItems?}` with `h` set: the client raised a change event for the
collection of watch `watch` (`list.watch`, §6.10). Events of one watch are sent in the order the client raised them, and
the event's `hostNs` (§4.4) is its host timestamp, so a host SHOULD send each event when it is raised.

| Member | Type | Meaning |
|---|---|---|
| `action` | string | A C# `NexusCollectionChangedAction` name: `Add`, `Remove`, `Replace`, `Move`, `Reset` (a clear or a completed reset) or `Ready` (the collection is synchronized). Never `Unset`. |
| `index` | int? | The index of the item after the change: `Add`, `Replace`, `Move`. |
| `oldIndex` | int? | The index of the item before the change: `Remove`, `Move`. |
| `items` | array? | The new items, typed JSON: `Add` and `Replace` (the new item), `Move` (the moved item). |
| `oldItems` | array? | The old items, typed JSON: `Remove` (the removed item), `Replace` (the replaced item). |

`index`, `oldIndex`, `items` and `oldItems` are omitted when the client runtime does not report them. The C# client
reports only `action`, so tests that run on every row assert on actions (and counts) only.

## 8. Errors

`error` is `{kind, message, detail?}`.

**Client error kinds** (the `ErrorKind` names; the runner rethrows them as the C# client's exceptions, `ErrorKinds`):

| Kind | Detail members | C# exception |
|---|---|---|
| `Transport` | `reason`: a `TransportError` name | `TransportException` |
| `RemoteInvocation` | `type`?: remote exception type name; `code`?: remote error code. `message` is the remote message: null (or absent) when the server sent none | `ProxyRemoteInvocationException` (`RemoteMessage` null when `message` is null or absent) |
| `Unauthorized` | | `ProxyUnauthorizedException` |
| `Canceled` | | `TaskCanceledException` |
| `Disconnected` | | `InvalidOperationException` "Session is not connected" |
| `Serialization` | | `NexusSerializationException` |
| `Busy` | | `NexusBusyException` |
| `TooLarge` | `size`, `limit` | `NexusMessageTooLargeException` |
| `InvalidOperation` | `type`? | `InvalidOperationException` |
| `ArgumentOutOfRange` | `param`?: the parameter name | `ArgumentOutOfRangeException` (`message` without .NET's " (Parameter 'x')" suffix) |

**Harness error kinds** (failures of the control layer, not of the client):

| Kind | Meaning | C# exception |
|---|---|---|
| `Timeout` | `timeoutMs` passed. | `TimeoutException` |
| `NotSupported` | The host or its client cannot do this (an option, a handler, a nexus pair). | `NotSupportedException` |
| `BadRequest` | Malformed arguments, an unknown op, a scope that is not open. A C# host reports any `ArgumentException` as `BadRequest` except `ArgumentOutOfRangeException`, which is the client kind `ArgumentOutOfRange`. | `ArgumentException` |
| `UnknownHandle` | §5. | `InvalidOperationException` |
| `Internal` | A bug in the host. `detail.stack` SHOULD hold the stack. | `RemoteHostException` |

Any detail MAY carry `stack` (the native stack trace, for the transcript). An unknown kind is treated as `Internal`.
`HostCrashed` never appears on the wire: the runner raises `HostCrashedException` itself (§9).

## 9. Supervision

- The runner sends `ping` every second. No response within 5 s, or the end of the host process, is a **crash**.
- On a crash the runner: captures a thread dump (`host.dumpThreads` when the host still answers, and for Java also
  `jcmd <pid> Thread.print`) and the last 200 lines of the host's stderr; fails every request in flight with
  `HostCrashedException` (carrying the dump and the stderr tail); kills the whole process tree; and launches a new
  host (new token). Handles of the old host are gone: drivers created before the crash fail with
  `HostCrashedException`.
- Tests marked `[RetryOnHostCrash]` are retried once, only when they failed with `HostCrashedException`.
- The runner keeps the last 500 messages of each test scope (requests, responses, events, with times) and attaches them
  as a TUnit artifact when the test fails.

## 10. Launch configuration (`host.json`)

The host builds write `artifacts/interop/<lang>/host.json` (repository root; `browser-*` languages fall back to
`artifacts/interop/browser/host.json`):

```json
{
  "command": "node",
  "args": ["clients/js/packages/interop-host-node/src/main.js"],
  "workingDir": ".",
  "env": {"NODE_OPTIONS": "--enable-source-maps"},
  "maxConcurrency": 8,
  "startupTimeoutMs": 60000
}
```

| Member | Type | Meaning |
|---|---|---|
| `command` | string | Executable (resolved on `PATH`, or relative to `workingDir`). Required. |
| `args` | string[] | Arguments before `--control … --token …` (and `--browser …`). |
| `workingDir` | string? | Relative to the repository root; default the root. |
| `env` | object? | Extra environment variables. |
| `maxConcurrency` | int? | Tests the runner runs at once on this language (the TUnit parallel limiter); default 4. Must not exceed the hello's `maxConcurrency`. |
| `startupTimeoutMs` | int? | How long the runner waits for `hello`; default 60 000. |

When the file is missing, every row of the language fails with the command that builds the host:

| Language | Build command |
|---|---|
| `node`, `browser-*` | `npm ci --prefix clients/js && npm run build:interop-host --prefix clients/js` |
| `java` | `clients/java/gradlew -p clients/java :interop-host:installInteropHost` |

(The JavaScript and Java host builds MUST provide these script/task names.)

## 11. Runner components (C#)

| Type | Role |
|---|---|
| `Remote.ControlFraming`, `ControlConnection`, `ControlListener`, `HostHello` | Framing, request/response correlation, events, transcripts, hello checks. |
| `Remote.ControlErrors`, `ControlJson`, `LeakReports` | Error objects (§8), the JSON of specs, method/collection references, steps and type expressions, leak reports. |
| `Remote.RemoteClientDriver` (`IClientUnderTest`), `RemotePoolDriver` (`IPoolUnderTest`), `RemoteClientDriverFactory` (`IClientDriverFactory`, `IPoolDriverFactory`) | Map the harness API onto the ops above (`RemotePoolDriver`: the pool ops of §6.11); rethrow error kinds as C# exceptions. |
| `Remote.RemoteTestAudit` | Opens scopes, and at the end of each TUnit test sends `test.end`, fails the test on a leak and attaches the transcript on failure. |
| `Hosts.HostLaunchConfig`, `SchemaHash` | `host.json` (§10), the expected schema hash (§4.6). |
| `Hosts.LanguageHost`, `LanguageHostPool` | Launch, warm-up, ping, crash capture, process-tree kill, restart. The pool is a TUnit `ClassDataSource` shared per test session; it registers each host's driver with `Harness`. |
| `Hosts.LanguageParallelLimitAttribute`, `RetryOnHostCrashAttribute` | Per-language parallel limit; retry once on a host crash. |
