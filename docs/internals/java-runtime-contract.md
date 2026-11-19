# Java runtime contract (generated code ↔ runtime)

Owner: K01. Consumers: the Java emitters (K03, K04, K11) and the runtime implementations (K02, K05–K10).
Frozen when K01 merges; a change needs a `C-<n>` amendment card (merge-train rules).

The Java client is `com.dtronix:nexnet-client`, package `com.dtronix.nexnet`, built in `clients/java`. This page
describes the API that generated code compiles against and the rules both sides follow. The JavaScript counterpart is
`docs/internals/js-runtime-contract.md`.

## 1. Build

| Item | Value |
|---|---|
| Build | Gradle wrapper (`clients/java/gradlew`, Gradle 9.8.2), Kotlin DSL, versions in `gradle/libs.versions.toml` |
| Gate | `clients/java/gradlew -p clients/java check` (JDK 21; no Android SDK) |
| Bytecode | `--release 11`, compiled with a JDK 21 toolchain, `-Xlint:all -Werror` |
| Android floor | API 24: `check` runs `animalSnifferMain` against `net.sf.androidscents.signature:android-api-level-24:7.0_r2` |
| Javadoc | Built in `check` with doclint (`all,-missing`) and `-Werror` |
| Tests | JUnit 5 (5.14.x, which still runs on Java 11). Test JVM: `-Pnexnet.testJava=11` or `21` (default 21); Gradle provisions a missing JDK through the Foojay resolver |
| Version | Read from `<Version>` in `src/NexNet/NexNet.csproj` (lockstep with NuGet and npm); exposed as `NexusRuntime.VERSION` |
| Jar | `Automatic-Module-Name: com.dtronix.nexnet`; sources and javadoc jars |
| Dependencies | None at run time. JSpecify (`org.jspecify:jspecify`) is a `compileOnlyApi` dependency (K28): it reaches consumers' compile classpath, so generated code and user code that see its `@Nullable` compile without declaring it, but it is not needed at run time |

The convention plugin `nexnet.java-library` (`clients/java/build-logic`) applies all of the above to every library.
Animal Sniffer is called through the `org.codehaus.mojo:animal-sniffer` library by the `AnimalSnifferCheck` task
rather than a third-party Gradle plugin, which used APIs Gradle 10 removes.

APIs absent on API 24 fail `check`: `java.net.http`, `java.util.concurrent.Flow` (API 30), `java.util.Base64`
(API 26), `java.time` (API 26), `SSLParameters.setApplicationProtocols` (API 29), and also
`Long.toUnsignedString`/`parseUnsignedLong`/`divideUnsigned` (API 26; use `UnsignedLongs`). Tests are not checked.

## 2. Packages

| Package | Contents | Implemented by |
|---|---|---|
| `com.dtronix.nexnet` | `CancellationToken`, `CancellationTokenSource`, `Registration`, `DisconnectReason`, `TransportError`, exceptions, value types (`NexusDateTime`, `NexusDateTimeKind`, `NexusDateTimeOffset`, `NexusTimeSpan`, `NexusDateOnly`, `NexusTimeOnly`, `NexusDecimal`, `NexusVersion`, `Tuple1`..`Tuple7`, `UnsignedLongs`), `NexusRuntime` | K01 (data, cancellation); K02 (conversions) |
| `com.dtronix.nexnet.serialization` | `Formatter<T>`, `MsgPackWriter`, `MsgPackReader`, `MsgPackType`, `MsgPackException`, `FormatterRegistry` | K02 |
| `com.dtronix.nexnet.spi` | Generated-code SPI: `ProxyBase`, `ProxyInvoker`, `ArgumentWriter`, `PipeArgumentWriter`, `ClientNexusBase`, `InvocationContext`, `HandshakeDescriptor`, `ClientNexusHooks` | K01 (bases); K07 (`ProxyInvoker`, `InvocationContext` implementations) |
| `com.dtronix.nexnet.client` | `NexusClient`, `ClientConfig`, `TlsOptions`, `TransportKind`, `ConnectionState`, `ConnectionResult`, `StateListener`, `ClientSessionContext` | K05 (session), K06 (transports) |
| `com.dtronix.nexnet.logging` | `NexusLogger`, `NexusLogLevel` (section 6.2) | K05 |
| `com.dtronix.nexnet.session` | Session core: `NexusSession`, `SessionOptions`, `MessageHandler`, `Frame`, `InboundFrame`, `ControlSender`, `ControlSource`, `SessionTimers`, `NexusExecutors`, `SerialExecutor`, `SessionCounters`, `SessionClosedException`, `ProgressProbe` (K17), `SessionObserver`, `SessionObservers`, `SessionMetricsSink` (K20). Runtime-internal (sections 6.5, 6.9 and 6.10) | K05 |
| `com.dtronix.nexnet.transport` | `Transport`, `Transport.Connector`, `NexusConnector`, `TlsTrust` (sections 6.3, 6.4, 6.8) | K06 |
| `com.dtronix.nexnet.invocation` | `InvocationRuntime`, `ClientProxyInvoker`, `InvocationOptions`, `NexusRemoteError`, `RemoteExceptionDetail`, `InvocationCounters`, `SessionBudget`, `PayloadCodec` (section 6.7) | K07 |
| `com.dtronix.nexnet.pipes` | `NexusDuplexPipe`, `NexusDuplexChannel<T>`, `NexusChannelReader<T>`, `NexusChannelWriter<T>` | K08 |
| `com.dtronix.nexnet.collections` | `NexusList<T>`, `NexusListChange<T>`, `NexusCollectionState`, `NexusCollectionMode` (C-4), `NexusListClient<T>` (the runtime implementation) | K09 |
| `com.dtronix.nexnet.reconnect` | `ReconnectionPolicy`, `DefaultReconnectionPolicy` (0/2/10/30 s) | K01 (done); K10 wires it |
| `com.dtronix.nexnet.pool` | `NexusClientPool`, `NexusClientPoolConfig`, `RentedNexusClient` | K10 |
| `com.dtronix.nexnet.diagnostics` | `Diagnostics` (leak-audit counters, `snapshot(client)`), `NexusSessionSnapshot`, `NexusInvocationSnapshot`, `NexusBudgetSnapshot`, `NexusPipeSnapshot`, `NexusLargeMessageSnapshot` and the other snapshot types (section 6.10) | K10, K20 |
| `com.dtronix.nexnet.metrics` | `NexusMetrics`, `NexusMetricsListener`, `MeasurementObserver`, `NexusInstruments`, `NexusInstrument`, `InstrumentKind`, `MetricTags` (section 6.10) | K10, K20 |

The value types and tuples live in the root package because the generator's type mapper (G03, `JavaTarget`) emits
`com.dtronix.nexnet.NexusDateTime`, `com.dtronix.nexnet.Tuple2` and so on. C# `decimal` maps to
`java.math.BigDecimal`; `NexusDecimal` is the codec's four-integer form.

Skeleton bodies throw `UnsupportedOperationException("not implemented")`. Pure data types (exceptions, enums,
tuples, value-type storage and equality, `HandshakeDescriptor`, `ClientConfig`, `TlsOptions`), cancellation and
`DefaultReconnectionPolicy` are implemented and tested.

## 3. What generated code uses

Generated code may use only the members listed here. Implementations may add public members (additive), but must
not change or remove these without an amendment card.

### 3.1 Formatters

```java
public interface Formatter<T> {
    void write(MsgPackWriter writer, @Nullable T value);
    @Nullable T read(MsgPackReader reader);
}
```

- K02 provides the built-ins (`com.dtronix.nexnet.serialization.Formatters`). Formatters are stateless and
  thread-safe. The generated formatters are described in section 3.1.1.
- `MsgPackWriter`: `writeNil`, `writeBoolean`, `writeInt(long)`, `writeUInt64(long bits)`, `writeFloat`,
  `writeDouble`, `writeString`, `writeBinary`, `writeArrayHeader`, `writeMapHeader`, `writeExtHeader(type, length)`,
  `writeRaw`, `size`, `toByteArray`.
- `MsgPackReader(byte[], offset, length)`: `peekType`, `tryReadNil`, `readBoolean`, `readLong`, `readInt`,
  `readUInt64`, `readFloat`, `readDouble`, `readString`, `readBinary`, `readArrayHeader`, `readMapHeader`,
  `readExtHeader`, `lastExtType`, `readRaw`, `skip`, `remaining`. Malformed input throws `MsgPackException`.
- K02 finalizes this API (it owns the formatter contract) and may add methods.

### 3.1.1 Generated types and formatters (K03)

The Java emitter writes one file per declared .NET type, and the formatters in a sub-package:

| File | Contents |
|---|---|
| `<package>/<Type>.java` | One flat package (`NexNetJavaPackage`). Nested .NET types are flattened to `Outer_Inner` |
| `<package>/formatters/Formatters.java` | A `public static final Formatter<X>` constant per non-generic type, named as the type (`Formatters.InteropItem`), and a factory per generic type (`Formatters.InteropBox(Formatter<T>)`). Abstract objects have none |
| `<package>/formatters/UserFormatters.java` | Only when a `[NexusTargetFormatter<T>(NexusTargets.Java, "fqcn")]` mapping exists (D12): one `Formatter<Object>` field per hand-written formatter class, instantiated with its public no-argument constructor. A missing class fails the Java compilation |

`Formatters`, `UserFormatters` and `formatters` are reserved type names (NEXNET041).

Type shapes:

- **Objects** (`[NexusObject]` classes, structs and records) are mutable classes: private fields, a no-argument
  constructor, an all-members constructor in key order, and `getX`/`setX` accessors. Every object has value
  `equals`, `hashCode` and `toString`, so it works in a `Set`, as a `Map` key and with a list's `remove`.
  Structs are `final`. A non-generic `[NexusObject]` base class is extended; a generic one has its members copied.
- **Members** that may be null are annotated JSpecify `@Nullable`. A field starts at its C# initializer when the IR
  records a literal one, otherwise at C# `default(T)`.
- **Wire form.** An object is a MessagePack array with nil in the key gaps. Reading accepts fewer elements (an older
  peer: the missing members are `default(T)`) and more (a newer peer: skipped). Struct types refuse null both ways.
- **Enums** are open value classes, not Java `enum`s: a constant per member, `value()`, and `of(v)` for any value
  of the underlying type. `[Flags]` enums add `or`, `and` and `has`.
- **Unions** over an abstract class are an abstract class, and unions over an interface are an interface. Case
  classes extend or implement them. A union is written as `[tag, value]`.
- **User-formatter types.** Members whose .NET type has a custom `[NexusFormatter]` are typed `Object`. They are
  read and written through `UserFormatters`.

### 3.2 Handshake

The emitter (K04) writes one constant per export from the IR (`handshake`, `clientAcceptedIds`):

```java
public static final HandshakeDescriptor DESCRIPTOR = new HandshakeDescriptor(
    "0.20.0",            // NexusRuntime.VERSION the code was generated for; major.minor must match the runtime
    "v2",                // handshake.version, or null when the server API is unversioned (max 16 chars)
    123456789,           // handshake.serverNexusHash (opaque; never recomputed)
    -987654321,          // handshake.clientNexusHash
    new int[] {0, 1, 4}  // clientAcceptedIds[].id; the hash is always clientNexusHash
);
```

### 3.3 Proxy

```java
public final class ChatServerProxy extends ProxyBase {
    public ChatServerProxy(ProxyInvoker invoker) { super(invoker); }

    // C# void Notify(string text): fire and forget
    public void notify(String text) {
        invokeVoid(0, w -> { w.writeArrayHeader(1); Formatters.STRING.write(w, text); });
    }

    // C# ValueTask Ping()
    public CompletableFuture<Void> ping() { return invokeTask(1, ArgumentWriter.NONE, null); }

    // C# ValueTask<int> Add(int a, int b, CancellationToken ct): two overloads
    public CompletableFuture<Integer> add(int a, int b) { return add(a, b, CancellationToken.NONE); }
    public CompletableFuture<Integer> add(int a, int b, CancellationToken cancellationToken) {
        return invoke(2, w -> { w.writeArrayHeader(2); Formatters.INT.write(w, a); Formatters.INT.write(w, b); },
            Formatters.INT, cancellationToken);
    }

    // C# ValueTask Upload(INexusDuplexPipe pipe, string name): the pipe is written as its initial id
    public CompletableFuture<Void> upload(NexusDuplexPipe pipe, String name) {
        return invokePipe(3, pipe, (w, pipeId) -> {
            w.writeArrayHeader(2); w.writeInt(pipeId); Formatters.STRING.write(w, name); });
    }

    // C# ValueTask Stream(INexusDuplexChannel<Message> channel)
    public CompletableFuture<Void> stream(NexusDuplexChannel<Message> channel) {
        return invokeChannel(4, channel, (w, pipeId) -> { w.writeArrayHeader(1); w.writeInt(pipeId); });
    }

    // C# [NexusCollection(NexusCollectionMode.BiDirectional)] INexusList<string> Names { get; }
    public NexusList<String> names() { return list(5, NexusCollectionMode.BI_DIRECTIONAL, Formatters.STRING); }
}
```

| C# return | Java return | `ProxyBase` method |
|---|---|---|
| `void` | `void` | `invokeVoid(id, args)` |
| `ValueTask` | `CompletableFuture<Void>` | `invokeTask(id, args, token)` |
| `ValueTask<T>` | `CompletableFuture<T>` (boxed) | `invoke(id, args, formatter, token)` |
| pipe parameter | `CompletableFuture<Void>` (sent) | `invokePipe(id, pipe, args)` |
| channel parameter | `CompletableFuture<Void>` (sent) | `invokeChannel(id, channel, args)` |
| `INexusList<T>` property | `NexusList<T>` | `list(id, mode, itemFormatter)` |

- `ArgumentWriter` writes the array header (`serializedCount`) and the arguments in `wireIndex` order;
  `ArgumentWriter.NONE` sends an empty argument payload (no serialized arguments).
- `PipeArgumentWriter` receives the runtime-assigned initial pipe id (1–255) and writes it at the pipe or channel's
  wire position. Pipe and channel methods are sent with `IgnoreReturn | DuplexPipe`.
- The token argument is null or `CancellationToken.NONE` when the method takes no token.
- Returned futures complete on the callback executor. `future.cancel(true)` sends `InvocationCancellation`.
- **Collections (amendment C-4).** `list(collectionId, mode, itemFormatter)` takes the collection's mode from the IR
  (`CollectionIr`, from the server nexus's `[NexusCollection(mode)]`): `UNSET`, `SERVER_TO_CLIENT`, `BI_DIRECTIONAL`
  or `RELAY`. The mode is not sent on the wire (specification section 12.2). The runtime returns the same instance
  for a collection id on every call, for the life of the client. See section 3.6.

### 3.4 Client nexus

```java
public abstract class ChatClientNexusBase extends ClientNexusBase<ChatServerProxy> {
    protected abstract CompletableFuture<Integer> square(int value, CancellationToken cancellationToken);
    protected abstract void message(String text);
    // C# ValueTask Receive(INexusDuplexPipe pipe) and ValueTask Feed(INexusDuplexChannel<Message> channel)
    protected abstract CompletableFuture<Void> receive(NexusDuplexPipe pipe);
    protected abstract CompletableFuture<Void> feed(NexusDuplexChannel<Message> channel);

    @Override
    protected CompletableFuture<Void> dispatch(int methodId, MsgPackReader args, InvocationContext ctx) {
        switch (methodId) {
            case 0: { args.readArrayHeader(); int v = Formatters.INT.read(args);
                      return ctx.result(square(v, ctx.cancellationToken()), Formatters.INT); }
            case 1: { args.readArrayHeader(); message(Formatters.STRING.read(args)); return ctx.noResult(); }
            // Pipe and channel methods return ctx.result(future): the runtime closes the pipe when it completes.
            case 2: { args.readArrayHeader(); return ctx.result(receive(ctx.pipe(args.readInt()))); }
            case 3: { args.readArrayHeader();
                      return ctx.result(feed(ctx.channel(args.readInt(), Message.FORMATTER))); }
            default: throw new IllegalArgumentException("Unknown method id " + methodId);
        }
    }
}
```

- Lifecycle hooks (override in user code): `onConnected(boolean isReconnected)`, `onDisconnected()`,
  `onReconnecting()`, each returning `CompletableFuture<Void>`. `context()` returns the `ClientSessionContext`.
- `InvocationContext.result(future, formatter)` for `ValueTask<T>`, `result(future)` for `ValueTask`,
  `noResult()` for `void` methods only. Pipe and channel arguments: `ctx.pipe(id)`, `ctx.channel(id, formatter)`.
- **Pipe and channel methods return `ctx.result(future)`** with the user method's future (amendment C-3). They
  are invoked with `IgnoreReturn | DuplexPipe`, so no result is sent, but the runtime closes the method's pipes
  (specification section 7.6) and finishes the invocation only when that future completes. `noResult()` completes
  at once, so it would close the pipe before the method has used it. A pipe method's execution slot is freed as soon
  as its pipe opens (specification section 8), so long-running pipe methods don't block other invocations.
- The runtime validates the id against `HandshakeDescriptor.acceptsMethod` before dispatch.
- Methods with `separatePool` in the IR: the generated base overrides `protected boolean separatePool(int methodId)`
  to return true for their ids; the runtime runs them in the separate invocation pool.
- The runtime calls the protected members through `ClientNexusHooks` (runtime-only; generated code never does).

### 3.5 Factory

```java
public final class ChatClient {
    public static NexusClient<ChatServerProxy> create(ClientConfig config, ChatClientNexusBase nexus) {
        return NexusClient.create(config, ChatHandshake.DESCRIPTOR, nexus, ChatServerProxy::new);
    }
}
```

### 3.6 Collections

`NexusList<T>` (`com.dtronix.nexnet.collections`), implemented by `NexusListClient<T>`, is the client replica of a
server `INexusList<T>` (protocol specification section 12):

- `enable()` returns `CompletableFuture<Boolean>`. It opens a collection pipe on the current session, and completes
  true after the initial reset, false if the pipe closes first. It fails with `NexusBusyException` when the server
  refuses (limit or authorization) and with `IllegalStateException` when not connected. `disable()`, `ready()`
  (a new future per enable, failed if that enable ends before its reset), `disabled()` and `state()`
  (`NexusCollectionState`).
- The `java.util.List` view is read-only. It reads an immutable snapshot (iterators keep theirs), and its mutators
  throw `UnsupportedOperationException`.
- Mutations return `CompletableFuture<Boolean>`: `addAsync`, `insertAsync`, `removeAsync(item)` (first item
  `equals` to it; false and nothing sent when none), `removeAtAsync`, `replaceAsync`, `moveAsync`, `clearAsync`.
  - Each completes true on the server's Ack, and false on Noop, on an Ack change that fails validation, or when the
    pipe closes.
  - One request is in flight at a time. A request is built from the snapshot current when its turn comes, and
    waits for the reset.
  - The future fails locally with `IllegalStateException` in a read-only mode or when not enabled, and with
    `IndexOutOfBoundsException` for a negative index.
- `addChangeListener(Consumer<NexusListChange<T>>)` returns a `Registration`. Changes are delivered in order on
  the callback executor:
  - ADD for Insert, REMOVE for Remove, REPLACE for Replace, MOVE for Move, and RESET for Clear;
  - RESET then READY after a reset;
  - RESET when the pipe closes.
- After a reconnect, collections are not resumed: the application calls `enable()` again and receives a full reset.
- Generated item formatters run with the session's payload codec. `ResetValues` arrays (array, `bin`, ext 78) are
  split by the runtime, which hands each element to the item formatter as one value.

## 4. User-facing API

- `NexusClient<TProxy>`: `connect()`/`connect(token)`, `tryConnect()`/`tryConnect(token)` (a
  `ConnectionResult`), `disconnect()`, `state()`, `addStateListener`, `disconnected()`, `proxy()`, `isSecure()`,
  `config()`, `session()` (the current `NexusSession`, or null), `createPipe()`, `createChannel(formatter)`,
  `close()`.
- `ClientConfig`: `webSocket(URI)` (ws/wss), `httpSocket(URI)` (http/https), `tcp(host, port)`,
  `tcpTls(host, port)`. UDS and QUIC are not available on Java (D4). Builder options:
  - `tls(TlsOptions)`;
  - `authenticate(Supplier<byte[]>)` or `authenticateAsync(Supplier<CompletableFuture<byte[]>>)` (mutually
    exclusive; `build()` throws `IllegalStateException` when both are set);
  - WebSocket and HttpSocket only (a TCP transport throws `IllegalStateException`): `header(name, value)` adds an
    upgrade request header (setting a name again, ignoring case, replaces it; Host, Connection, Upgrade,
    `Sec-WebSocket-*`, Content-Length and Transfer-Encoding are reserved; CR, LF and other control characters are
    rejected), `authorization(value)` sets `Authorization` (the C# `AuthenticationHeader`), and
    `queryToken(name, value)` appends `name=value`, URL-encoded, to the request target. Read back with
    `upgradeHeaders()` (unmodifiable, in insertion order), `queryTokenName()` and `queryTokenValue()`;
  - `callbackExecutor`, `invocationExecutor` (section 6.1), `reconnectionPolicy`, `logger(NexusLogger)`
    (section 6.2);
  - the timeouts, as in C#: `connectionTimeoutMillis` 50 s, `handshakeTimeoutMillis` 15 s, `timeoutMillis` (receive)
    30 s, all positive; `pingIntervalMillis(@Nullable Integer)`, null by default (section 6.6);
    `progressTimeoutMillis`, 30000 by default (`DEFAULT_PROGRESS_TIMEOUT_MILLIS`, C# `ProgressTimeout`), 50 to
    600000 (the builder method throws `IllegalArgumentException` outside that range): it bounds large messages, pipes whose
    close is not confirmed, channel items given extra credit and outbound transport writes; the session's keepalive
    tick checks them and disconnects with `Stalled` (37) (section 6.9);
  - `invocationOptions(InvocationOptions)`, `InvocationOptions.defaults()` unless set (section 6.7), read back with
    `invocationOptions()`; `pipeOptions(PipeOptions)`: `maxPipes` 32 and `pipeWindow` 256 KiB by default (C#
    `MaxPipes`/`PipeWindow`).
- Channels: `NexusChannelReader<T>`: `read()` (`Optional<T>`, empty at the end; **only for item types that are
  never null**: a null item fails it with `NullPointerException`), `readItem()` (`ChannelReadResult<T>` with
  `isCompleted()` and `value()`, which tells a null item apart from the end), `readBatch(max)`,
  `readUntilComplete()`. `NexusChannelWriter<T>`: `write`, `writeAll`, `complete`, `writeAndComplete`. When an
  item is larger than the server accepts (`NexusMessageTooLargeException`) or can't be serialized, the items before
  it in the same call are sent, as in C#; it and the items after it are not, the future fails, and
  `writeAndComplete` does not complete the channel. The channel stays usable.
- `TlsOptions`: `sslContext` (takes precedence over the managers), `trustManager`, `trustCaCertificates(byte[],
  includeSystemTrust)` (PEM or DER), `trustCaCertificatesPem(String, includeSystemTrust)`, `keyManager` and
  `clientCertificatePkcs12(byte[], char[])` (client certificate), `pinCertificateSha256(byte[32])`,
  `hostnameVerification` (on by default), `serverName` (SNI and host name verification). A pin is the SHA-256 of a
  chain certificate's DER encoding **or** of its DER SubjectPublicKeyInfo (section 6.4).
- Exceptions: `NexusException` (base, unchecked), `NexusTransportException` (with `TransportError`),
  `NexusRemoteInvocationException` (`remoteErrorCode`, `remoteExceptionType`, `remoteMessage`),
  `NexusUnauthorizedException` (a remote invocation exception), `NexusBusyException`,
  `NexusMessageTooLargeException` (`size`, `limit`). Cancellation uses `java.util.concurrent.CancellationException`.

## 5. Behavior both sides rely on

- **Threads.** One reader and one writer thread per connection (`nexnet-<id>-reader`, `nexnet-<id>-writer`; section 6.1). No IO on
  the caller thread. **Never complete a user-visible future on the reader thread**: complete it through the callback
  executor.
- **Security (F05, D7).** `isSecure()` is true only for `tcpTls`, `wss://` and `https://`. A plaintext client logs
  once per instance, on the first connect: `NexNet: unencrypted transport (<transport>); credentials and data are
  visible on the network.`
- **Authentication.** The supplier runs before the transport connects, on every connect and reconnect, bounded by the
  handshake timeout. If it throws or times out, the connect fails with `DisconnectReason.AUTHENTICATION` and no
  connection is opened.
- **Cancellation (D17).** A token canceled before send fails the call with `CancellationException` and sends nothing.
  A cancel after send completes the future canceled at once and the late result is consumed.
- **Version check.** `NexusClient.create` refuses a `HandshakeDescriptor.runtimeVersion()` whose major.minor differs
  from `NexusRuntime.VERSION`.
- **Flow control.** Pipe and channel credit is granted only when the consumer reads.

## 6. Runtime internals and extension points

Sections 3 to 5 are what generated code and applications use. This section covers the rest of the public surface:
the runtime's own layers, custom transports, and the options that tune them. Everything in
`com.dtronix.nexnet.session` and most of `com.dtronix.nexnet.invocation` is runtime-internal. It is public only
because the layers live in different packages. Generated code never uses it.

### 6.1 Threading model

| Thread or executor | Name | Runs |
|---|---|---|
| Reader, one per connection (daemon) | `nexnet-<client id>-reader` | Blocking reads of `Transport.input()`. Parses frames and dispatches them synchronously to the `MessageHandler`, in wire order. It never waits on a send: its control messages go through the `ControlSender`, and `send` does not block it when the queue is full |
| Writer, one per connection (daemon) | `nexnet-<client id>-writer` | Drains the bounded frame queue (`SessionOptions.writeQueueCapacity`, 1024 frames), then the `ControlSender`, and writes and flushes `Transport.output()`. Other senders block while the queue is full |
| Shared pool, `NexusExecutors.shared()` | `nexnet-pool-<n>` | A cached pool of daemon threads; idle threads retire after 60 s. Runs every connection attempt (token supplier, `Transport.Connector.connect`, handshake wait), so no IO happens on the thread that calls `connect()`. Also runs each session's outbound send lane (a `SerialExecutor`, which keeps calls from one thread in order). It is the default callback and invocation executor |
| Timer, `SessionTimers.system()` | `nexnet-timer` | Handshake timeout, receive (idle) timeout and the ping tick. Tasks are short and never block. Tests pass a manual `SessionTimers` |
| Callback executor, `ClientConfig.callbackExecutor` | default: shared pool | Completes every user-visible future: `connect`/`tryConnect`, call results and failures (including "not connected"), `disconnected()` and `NexusSession.closed()`. Also runs `StateListener`s, through a `SerialExecutor`, so they see changes in order even on a multi-threaded executor. On Android, pass the main-thread executor |
| Invocation executor, `ClientConfig.invocationExecutor` | default: shared pool | Runs the client nexus: `dispatch` (server-to-client invocations) and the lifecycle hooks `onConnected`, `onDisconnected` and `onReconnecting`. Also cancels running invocations when the session ends. `InvocationOptions` bounds how many invocations run at once (section 6.7), not the executor |

Rules:

- **Never complete a user-visible future on the reader, writer or timer thread.** `NexusSession.start()`,
  `send(Frame)` and `closedInternal()` are runtime-internal futures that complete on those threads. A layer must
  hop to an executor before it hands anything to user code.
- **Rejected tasks.** `NexusExecutors.execute(executor, task)` runs the task on the shared pool when the executor
  rejects it (one that was shut down, say), so no completion is lost.
- **Connect order.** `onConnected` runs on the invocation executor, and the attempt waits for its future. The proxy
  works from `onConnected` on. The server's invocations are dispatched only after `onConnected` completes.
- **`close()`** is the only `NexusClient` method that blocks: it waits up to
  `SessionOptions.threadJoinTimeoutMillis` (5 s) for the reader and writer threads (`NexusSession.awaitThreads()`).

### 6.2 Logging

`ClientConfig.Builder.logger(NexusLogger)` sets the logger. Without one the runtime logs nothing, and that includes
the plaintext warning (D7). It mirrors the C# `INexusLogger`:

```java
@FunctionalInterface
public interface NexusLogger {
    void log(NexusLogLevel level, @Nullable String category, @Nullable Throwable exception, String message);
    default boolean isEnabled(NexusLogLevel level) { return level != NexusLogLevel.NONE; }
    default NexusLogger createLogger(@Nullable String pathSegment) { return this; }
}
```

- **Levels.** `NexusLogLevel` is `TRACE`, `DEBUG`, `INFORMATION`, `WARNING`, `ERROR`, `CRITICAL` and `NONE`,
  as in C#. `NONE` is not a message level: a logger at that level writes nothing.
- **Per-client logger.** Each client logs through `logger.createLogger("CL<id>")`, where `<id>` is a process-wide
  counter. The client, its sessions and its invocation and pipe runtimes all use that logger.
- **Categories.** The `category` argument names the layer: `Client`, `Session`, `Invocation` and so on.
- **What the runtime expects from a logger.** It checks `isEnabled` before it builds a message, and it ignores a
  `RuntimeException` thrown by `log`. Implementations must be thread-safe and must not block: the runtime logs from
  the reader, writer and timer threads.
- **Notable messages:**
  - the plaintext warning (`WARNING`, once per client, on the first connect; section 5);
  - the ping-interval warning (`WARNING`, section 6.6);
  - `Disconnected: <reason>.` (`INFORMATION`);
  - a failed client nexus method: `ERROR` with the exception, or `WARNING` for `NexusBusyException` and
    `NexusMessageTooLargeException`;
  - a failed token supplier or `onConnected`/`onDisconnected` hook (`ERROR`).

### 6.3 Custom transports

```java
NexusClient<P> client = NexusClient.create(config, Handshake.DESCRIPTOR, nexus, ServerProxy::new, connector);
```

The generated factory (section 3.5) calls the four-argument overload, which uses `NexusConnector.defaults()`. The
five-argument overload takes any `Transport.Connector`, for a test transport, a proxy tunnel, or OkHttp (K13).

- **`Transport.Connector.connect(ClientConfig config, int timeoutMillis)`** is called once per connection attempt,
  reconnects included. It runs on a runtime thread, never on the caller of `connect()`, after the token supplier has
  run. `timeoutMillis` is what is left of `connectionTimeoutMillis`. It returns a `Transport` that is ready for
  the NexNet preamble. To fail, it throws an `IOException`, mapped as in section 6.8, or a
  `NexusTransportException` carrying its `TransportError`.
- **`Transport` members:**
  - `input()`: blocking reads, end of stream when the connection closes. Only the reader thread reads it.
  - `output()`: blocking writes; `flush()` pushes buffered bytes to the network. Only the writer thread writes it.
    The streams therefore need not be thread-safe.
  - `name()`: used in logs, for example `tcp` or `wss://`.
  - Defaults: `isSecure()` false, `remoteCertificate()` null, `remoteAddress()` null, `remotePort()` -1.
  - `close(DisconnectReason)`: idempotent, never throws, callable from any thread, and must unblock a read or write
    in progress. When a disconnect byte is due, the session has already written it.
- **Byte stream, not messages.** NexNet frames may span any number of reads and writes. Bytes that arrived with an
  upgrade response (HTTP `101`) belong to the session: `input()` returns them first.
- **What a transport does not control.** The plaintext warning, `NexusClient.isSecure()` and
  `ClientSessionContext.isSecure()` come from `ClientConfig.isSecureTransport()` (the configured scheme or kind),
  not from `Transport.isSecure()`. `ClientSessionContext.remoteCertificate()`, `remoteAddress()` and
  `remotePort()` report the transport's values.

### 6.4 Default connector, TLS trust and pins

**`NexusConnector`** (`com.dtronix.nexnet.transport`) implements the built-in transports (D4):

| Transport | Behavior |
|---|---|
| TCP and TCP with TLS | Plain socket, or TLS on it |
| HttpSocket (`http://`, `https://`) | Sends an HTTP/1.1 upgrade with `Connection: Upgrade` and `Upgrade: nexnet-httpsockets`, expects `101`, then carries the raw stream |
| WebSocket (`ws://`, `wss://`) | RFC 6455, version 13. Binary messages, no subprotocol and no extension requested. The response must carry `Upgrade: websocket`, `Connection: Upgrade` and the right `Sec-WebSocket-Accept`, and must select no subprotocol or extension |

Every connection:

- sets `TCP_NODELAY`;
- for TLS, sends SNI for host names, negotiates no ALPN, and verifies the host name unless
  `TlsOptions.hostnameVerification(false)` turns that off;
- fits the socket connect, the TLS handshake and the upgrade into the connection timeout;
- for the upgrade transports, accepts a response head of at most 16 KiB.

`NexusConnector.defaults()` is the shared connector without extra headers. `NexusConnector.builder()` adds
upgrade-request data on top of the `ClientConfig` options:

```java
Transport.Connector connector = NexusConnector.builder()
    .header("X-Tenant", "a")              // reserved names and control characters are rejected, as in ClientConfig
    .authorization("Bearer " + jwt)       // header("Authorization", ...)
    .queryToken("access_token", token)    // appended URL-encoded; prefer a header, query strings are often logged
    .build();
```

The upgrade request carries the `ClientConfig` headers, then the connector's headers. If both set the same header
(ignoring case), the connector's value wins. If both set a query-string token, the connector's replaces the
configuration's.

**`TlsTrust`** builds what `TlsOptions` takes:

| Method | Result |
|---|---|
| `systemTrustManager()` | The platform trust manager |
| `fromCaCertificates(InputStream, includeSystemTrust)` | Trusts the CA certificates in the stream: PEM (one or more `BEGIN CERTIFICATE` blocks) or DER. It fails when the stream holds none |
| `fromKeyStore(KeyStore, includeSystemTrust)` | Trusts the certificate entries of a key store |
| `keyManagerFromPkcs12(InputStream, char[])` | A client certificate and its key from a `.pfx`/`.p12` |
| `keyManagerFromKeyStore(KeyStore, char[])` | Client certificates from the key entries of a key store |
| `certificateSha256(X509Certificate)` | SHA-256 of the certificate's DER encoding (a certificate pin) |
| `spkiSha256(X509Certificate)` | SHA-256 of its DER SubjectPublicKeyInfo (an SPKI pin; its base64 is the HPKP `pin-sha256` form, and it survives renewal with the same key) |

- **`includeSystemTrust`.** When true, a chain is accepted if either the custom CAs or the platform accepts it.
- **`TlsOptions` shortcuts.** `trustCaCertificates`, `trustCaCertificatesPem` and `clientCertificatePkcs12` call
  these methods for you.
- **Pins.** Each pin is 32 bytes (`pinCertificateSha256`). It matches a chain certificate (the leaf or any
  certificate the server sent) when it equals either form above, and both forms are accepted for every pin. With pins
  set, the connection is accepted only when some pin matches some chain certificate. Pins apply **in addition to**
  the trust manager's verification. With no pins, there is no pinning.

### 6.5 Session attachment points (`com.dtronix.nexnet.session`)

`NexusClient` creates one `NexusSession` per connection attempt and attaches the layers **before** `start()`,
so frames that arrive in the same read as the `ServerGreeting` find their handler:

```java
NexusSession s = NexusSession.builder(transport, handshake)
    .name(clientId).authenticationToken(token).reconnecting(isReconnect)
    .options(sessionOptions).handshakeTimeoutMillis(..).timeoutMillis(..).pingIntervalMillis(..)
    .progressTimeoutMillis(config.progressTimeoutMillis())                        // K17, section 6.9
    .callbackExecutor(callbacks).timers(timers).logger(logger)
    .owner(client)                                                                 // K20, section 6.10
    .build();
InvocationRuntime runtime = InvocationRuntime.builder(s, handshake, nexus)
    .options(config.invocationOptions())                                           // K17
    .callbackExecutor(callbacks).invocationExecutor(invocations).logger(logger).build();
s.setMessageHandler(runtime);                                                     // K07
PipeManager.attach(runtime, callbacks, timers, config.pipeOptions(), logger);   // K08: next handler; K19: progress probe
s.start();   // handshake; then runtime.connected(), invoker.attach(runtime), onConnected, runtime.startDispatching()
```

| Member | Contract |
|---|---|
| `setMessageHandler(MessageHandler)` | Receives every message after the handshake except `Ping`, disconnects and `ServerGreeting`. Delivery is on the reader thread, in wire order. `onMessage(InboundFrame)` must not block. To end the session for a protocol violation it throws `SessionProtocolException(reason)`. `onClosed(reason)` is called once, on the thread that closed the session. The default handler, `MessageHandler.UNHANDLED`, ends the session with `PROTOCOL_ERROR`. Handlers chain: `InvocationRuntime.setNextHandler` passes on the types it does not handle (pipes, then collections), together with `onClosed` |
| `send(Frame)` | Queues a frame (`Frame.of(type, body)`, `Frame.of(type, postHeader, body, offset, length)`, `Frame.headerOnly(type)`; `Frame.MAX_BODY_LENGTH` is 0xFFFF). The runtime-internal future completes on the writer thread once the frame is written and flushed. It fails with `SessionClosedException` (carrying the `DisconnectReason`) when the session has closed, or with `IllegalStateException` before the handshake completes. While the queue is full, callers block, except the reader thread |
| `controlSender()` | The coalescing `ControlSender` (C# `SessionControlSender`). `requestPing()` keeps at most one ping pending. `returnInvocationCredit(n)` batches credit until half the advertised credits are pending. `requestInvocationCancellation(id)`, `requestAbort(id, kind, reason)` (kind 0 argument or 1 result; reason 0 Busy, 1 TooLarge, 2 Cancelled, 3 Ack) and `requestControl(ControlSource)` (pipe control) complete the set. Requests only record what is owed and wake the writer, which appends the pending frames after the queued frames of each batch |
| `disconnect(reason)` / `abort(reason)` | Close with a disconnect byte, or at once without one (an abandoned attempt). Closing with a byte waits for the writer to send it, but at most the progress timeout plus `disconnectDelayMillis`; then the transport is closed anyway (section 6.9) |
| `addProgressProbe(ProgressProbe)` | Adds a stall check, asked on every keepalive tick (section 6.9). `nanoTime()` is the session clock the probes are given; `progressTimeoutMillis()` the configured timeout |
| `addMetricsSink(SessionMetricsSink)` / `recordMetric(instrument, value, tags)` | The session's metrics hook (section 6.10) |
| `closed()` / `closedInternal()` | Complete with the `DisconnectReason`, on the callback executor or on the closing thread |
| Read-only state | `id()`, `name()`, `transport()`, `isConnected()`, `isClosing()`, `disconnectReason()`, `isSecure()`, `remoteCertificate()`, `options()`, `peerMaxMessageSize()`, `sendMaxMessageSize()` (the smaller of both sides' limits), `peerInvocationCredits()`, `advertisedIdleTimeoutMillis()` (-1 when none), `pingIntervalMillis()`. The peer values are -1 before the handshake completes. Also `isEstablished()` (true once the server's greeting was accepted; it stays true while closing), `owner()` (the `Builder.owner` value, the `NexusClient` for client sessions), `messageHandler()` and the diagnostic readings of section 6.10 |

`InboundFrame` exposes `type()`, `body()` and the post-header fields `postHeader()`, `pipeId()`,
`chunkInvocationId()` and `chunkKind()`. `SessionTimers` has `nanoTime()` and
`schedule(task, delayMillis)`, which returns a `Registration` that cancels the task.

**`SessionOptions`** holds the limits this client advertises in its greeting and the session timings. They are not
on `ClientConfig`: `NexusClient` uses `SessionOptions.defaults()`, the C# untrusted defaults (protocol
specification section 9). The package-private `create` overload that tests use takes others. The options are
immutable; each `withX(value)` returns a copy:

| Option | Default | Range | Meaning |
|---|---|---|---|
| `maxMessageSize` | 4 MiB | at least 65 526 (`MIN_MAX_MESSAGE_SIZE`, one frame) | Largest argument, result or channel item this client accepts. Advertised |
| `invocationCredits` | 32 | 1–65535 | How many invocations the server may have queued at this client. Advertised |
| `disconnectDelayMillis` | 200 | 0–10000 | Wait after writing a disconnect byte before closing the transport |
| `writeQueueCapacity` | 1024 | at least 1 | Frames that may wait for the writer before senders block |
| `threadJoinTimeoutMillis` | 5000 | at least 0 | How long closing waits for the reader and writer threads |

The server's advertised limits are checked at the handshake: a `maxMessageSize` below 65 526 or
`invocationCredits` below 1 ends the session with `PROTOCOL_ERROR`. Other fixed limits:

- `NexusSession.MAX_PRE_AUTH_BODY_LENGTH` (16 KiB): the largest body accepted before the connection completes;
- `MAX_AUTHENTICATION_TOKEN_LENGTH` (8192): the largest token the server accepts.

`SessionCounters` (`openSessions`, `liveThreads`, `activeTimers`, `pendingWrites`) and `InvocationCounters`
(`pendingCalls`, `inboundInvocations`, `largeMessages`) are process-wide. Every counter returns to 0 once every
client has closed, which is what the leak audit (H05) checks.

### 6.6 Ping interval (S04)

`ClientConfig.pingIntervalMillis()` is an `@Nullable Integer`. It is null by default; set values must be
positive. The effective interval (`NexusSession.pingIntervalMillis()`, C# `ClientConfig.GetEffectivePingInterval`)
is:

1. a configured interval, used as is. If it is not below the server's advertised idle timeout, the client logs a
   `WARNING` on connect, because the server may time the session out between pings;
2. otherwise, a third of the `idleTimeoutMs` the server advertises in `ServerGreeting`, at least
   `NexusSession.MIN_DERIVED_PING_INTERVAL_MILLIS` (1000 ms);
3. otherwise, when the server advertises no idle timeout, `ClientConfig.DEFAULT_PING_INTERVAL_MILLIS` (10 s).

Only the client pings. The receive timeout (`timeoutMillis`, 30 s) is checked on each ping tick, before the ping.

### 6.7 Invocation runtime (`com.dtronix.nexnet.invocation`)

`InvocationRuntime` is the per-session `ProxyInvoker` and `MessageHandler`. It handles both directions,
invocation credit, cancellation, and large (streamed) arguments and results (protocol specification sections 4.3 to
4.10, 6 and 8). The proxy's `ClientProxyInvoker` lasts as long as the client:

- `attach(runtime)` routes calls to a session once it is connected; `detach(runtime)` stops when that session
  closes;
- without a runtime, calls fail with `IllegalStateException("The client is not connected.")`. Futures fail on the
  callback executor; `invokeVoid` throws.

Public types:

| Type | Use |
|---|---|
| `NexusRemoteError(int code, @Nullable String message)` | An application error returned from `ClientNexusBase.onInvocationException`; C# `NexusRemoteError` |
| `RemoteExceptionDetail` | `NONE` (default): the server sees a failure without detail. `TYPE_AND_MESSAGE`: the class name and message, truncated to 256 and 2048 chars. Use `TYPE_AND_MESSAGE` only with a trusted peer |
| `InvocationOptions` | Immutable `withX` copies of `defaults()`, the C# untrusted defaults: `maxConcurrentInvocations` 2 (1–1000), `maxSeparatePoolInvocations` 4 (1–1000), `sessionBudget` 16 MiB (at least 64 KiB), `remoteExceptionDetail` `NONE`. Set through `ClientConfig.Builder.invocationOptions`, which `NexusClient` passes to `InvocationRuntime.Builder.options` |
| `SessionBudget` | The session's receive memory (`tryReserve`, `release`, `reserved`, `limit`; thread-safe). The invocation queue (`invocationCredits` x 64 KiB) is reserved up front. A large argument or result that does not fit the rest is refused with `Busy`. The pipe runtime reserves its windows from it. `InvocationRuntime.budget()` |
| `PayloadCodec` | Turns argument writers and result formatters into bytes and back. `PayloadCodec.MSGPACK` is the default; tests substitute their own through `InvocationRuntime.Builder.codec`. The runtime writes the message envelopes itself |
| `InvocationCounters` | Diagnostic counters (section 6.5) |
| `InvocationRuntime` | Builder `builder(session, handshake, nexus)` with `options`, `codec`, `callbackExecutor`, `invocationExecutor` and `logger`. Lifecycle `connected()` (opens the credit gate with the server's credits) and `startDispatching()`. Attachment points for the pipe (K08) and collection (K09) runtimes: `attachPipes`/`pipes()`, `setNextHandler`, `invokeIgnoreReturn(methodId, flags, arguments)` (flags must include `IgnoreReturn`), `budget()`, `session()` and `codec()`. Per-session counts `pendingCalls()` and `inboundInvocations()`. Diagnostics (section 6.10): `snapshot(nowNanos)`, `budgetSnapshot()`, `largeMessageSnapshots(nowNanos)`, `queued(separate)`, `executing(separate)`, `creditWaits()`, `largeMessagesInbound()` and `largeMessagesOutbound()`. A progress probe (section 6.9) watches the large arguments and results being received |
| `ClientProxyInvoker` | The proxy's invoker, described above |

**Failed client nexus methods.** When a method that returns a result fails, the runtime calls
`ClientNexusBase.onInvocationException(Throwable exception, int methodId)`, through
`ClientNexusHooks.invocationException`, on a runtime thread:

- a non-null `NexusRemoteError` is sent as the remote error code and message, whatever `remoteExceptionDetail`
  says;
- null (the default) sends what `remoteExceptionDetail` selects;
- a hook that throws is logged and treated as null.

The hook is not called for canceled invocations (the result is `Canceled`) or for fire-and-forget methods. A
result larger than the server's `maxMessageSize` is sent as a failure with `NexusMessageTooLargeException`
passed to the hook.

**Session end (C-2).** When the session closes, for any reason:

- every outbound call still pending completes **canceled**, on the callback executor. This covers calls waiting for
  credit, calls being sent and calls awaiting their result. `future.get()` then throws a
  `java.util.concurrent.CancellationException` ("The session closed."). Its `getCause()` is a
  `SessionClosedException` whose `reason()` is the session's `DisconnectReason` (K17), so an application can
  tell a call the session ended (`STALLED`, `TIMEOUT`, `SOCKET_ERROR` and so on) from one it canceled itself,
  which has no such cause.
- `invokeIgnoreReturn` futures still waiting for credit fail with the same exception.
- outgoing large messages are aborted. Server invocations still queued are dropped, and their budget is released.
- the `CancellationToken` of every running client nexus method is canceled, on the invocation executor.
- the next handler (pipes) receives `onClosed(reason)`.

### 6.8 Transport error mapping

A failed connect completes `connect()` exceptionally, with a `NexusTransportException` whose `error()` is the
`TransportError`. `tryConnect()` returns a `ConnectionResult` with `DISCONNECTED` status and the exception
instead. The `DisconnectReason` is `AUTHENTICATION` for `AUTHENTICATION_ERROR` and `SOCKET_ERROR` for
everything else, as in the C# client.

| Failure | `TransportError` |
|---|---|
| Upgrade answered `401` or `403` (HttpSocket, WebSocket) | `AUTHENTICATION_ERROR` |
| Upgrade answered `500` | `INTERNAL_ERROR` |
| Upgrade answered any other status except `101` | `PROTOCOL_ERROR` |
| Server closed the connection in the middle of the upgrade response head | `CONNECTION_ABORTED` |
| Malformed or oversized (above 16 KiB) response head; wrong WebSocket `Upgrade`/`Connection`/`Sec-WebSocket-Accept`; a subprotocol or extension selected | `PROTOCOL_ERROR` |
| TLS: no X.509 server certificate, host name mismatch, no pin matches, or any `SSLException` (untrusted chain, handshake failure) | `AUTHENTICATION_ERROR` |
| TLS context cannot be built from the options | `INTERNAL_ERROR` |
| `ConnectException` | `CONNECTION_REFUSED` |
| `SocketTimeoutException` (connect, TLS or upgrade past the connection timeout) | `CONNECTION_TIMEOUT` |
| `NoRouteToHostException` | `UNREACHABLE` |
| `UnknownHostException`; a configuration without a host or URI | `INVALID_ADDRESS` |
| Any other `IOException` | `INTERNAL_ERROR` |

Two failures sit outside this table:

- A server that refuses the handshake (a disconnect byte such as `CLIENT_MISMATCH`) fails `connect()` with a
  `NexusException` naming the `DisconnectReason`. `tryConnect()` reports it in the result.
- A connection that does not complete its handshake within `connectionTimeoutMillis` fails with
  `CONNECTION_TIMEOUT` and `DisconnectReason.TIMEOUT`.
- A token supplier that throws, or an async supplier that times out (`AUTHENTICATION_ERROR`), fails the attempt with
  `DisconnectReason.AUTHENTICATION` before any connection is opened.

After the handshake, transport failures end the session with a `DisconnectReason` and are not reported as
`TransportError`. A WebSocket transport closes with code 1002 for `PROTOCOL_ERROR` and 1000 otherwise.

### 6.9 Progress timeout and stall detection (K17, K18)

`ClientConfig.progressTimeoutMillis` (section 4) is the C# `ProgressTimeout` (protocol specification section 9).
The session checks progress on each keepalive (ping) tick while it is connected, after the receive timeout and
before the ping, so a stall is noticed within one ping interval of the timeout. The first stall found is logged
as a `WARNING` ("The session made no progress within <n> ms: <description>.") and closes the session with
`DisconnectReason.STALLED` (37). The checks, in order:

1. **Outbound transport write (K18).** The writer thread records when each transport write (and its flush) starts.
   A write that runs longer than the progress timeout is a stall. The session then closes **without** the disconnect
   byte, because the writer that would send it is the one that is stuck. Closing the transport unblocks the write,
   and the frames it was writing fail with `SessionClosedException(STALLED)`.
2. **Progress probes**, in the order they were added. Each one that reports a stall closes the session with the
   disconnect byte.

```java
@FunctionalInterface
public interface ProgressProbe {                       // com.dtronix.nexnet.session
    @Nullable String findStalled(long nowNanos, long timeoutNanos);  // a log description, or null
}
```

- `NexusSession.addProgressProbe(probe)` adds one; there is no removal, since a probe lives as long as its session.
  `nowNanos` is `NexusSession.nanoTime()` (the `SessionTimers` clock) and `timeoutNanos` the progress timeout.
- Probes run on the timer thread. They must be quick, must not block and must be thread-safe. A probe that throws a
  `RuntimeException` is logged as an `ERROR` and skipped.
- The runtime's own probes are:
  - the invocation runtime's: a large argument or result being received with no chunk for longer than the
    timeout;
  - the pipe runtime's (`PipeManager`): a pipe whose close the server never confirms, or a channel item given extra
    credit whose data stopped arriving.

**Close fallback (K18).** A close that sends the disconnect byte leaves the writer to send it, then waits
`SessionOptions.disconnectDelayMillis` before closing the transport. If the writer is stuck, closing waits at most
the progress timeout plus `disconnectDelayMillis`. It then logs a `WARNING` ("The disconnect byte was not written
within the progress timeout; closing.") and closes the transport, so a stalled writer cannot keep a session in
`CLOSING` forever.

### 6.10 Snapshots and metrics hooks (K20)

The diagnostics (`com.dtronix.nexnet.diagnostics`) and metrics (`com.dtronix.nexnet.metrics`) layers read the
session, invocation and pipe layers through these hooks. They are public for those layers. Applications use
`Diagnostics` and `NexusMetrics`.

**Session readings (`NexusSession`):**

| Member | Value |
|---|---|
| `isEstablished()` | True once the server's greeting was accepted (the handshake completed); it stays true after that |
| `owner()` | The `Builder.owner(Object)` value: the `NexusClient` for client sessions, null otherwise |
| `connectedNanos()` | `nanoTime()` when the session started, right after its transport connected; 0 before `start()` |
| `lastReceivedNanos()` | When a complete frame was last received (the start time until one is) |
| `hasSent()`, `lastSentNanos()` | Whether a transport write has started, and when the last one did |
| `isFlushing()`, `flushStartedNanos()` | Whether a transport write (and its flush) is in progress, and when it started |
| `framesSent()`, `bytesSent()`, `framesReceived()`, `bytesReceived()` | Transport totals, below |

Counting rules, as in C#:

- **Bytes** count everything on the wire: frame headers, post-headers and bodies, the preamble and the disconnect
  byte.
- **Frames.** The preamble is not a frame, in either direction. A disconnect byte is one frame, sent or received.
  Received pings and the control frames the writer appends (credit, cancellation, ping, pipe control) are frames.
- Sent values are counted when the bytes are handed to the transport, before the flush. They are written by the
  writer thread only, and received values by the reader thread only. Readers see them through `volatile` reads.

**Session start observer.** `SessionObservers.add(SessionObserver)` registers a process-wide observer and returns
a `Registration` that removes it. `SessionObserver.started(session)` is called synchronously from
`NexusSession.start()`, on the thread that starts the session, after the transport connected and before the
reader and writer threads start. A session that ends during the handshake is therefore still seen. It must be cheap,
and a `RuntimeException` it throws is ignored.

**Session metrics sink.** `NexusSession.addMetricsSink(SessionMetricsSink)` returns a `Registration` that detaches
it. `hasMetricsSinks()` lets a layer skip the work of a measurement. The layers report event instruments with
`recordMetric(NexusInstrument, double value, Map<String, String> tags)`, which does nothing without a sink:

- `value` is a counter increment or a histogram value (seconds for durations);
- `tags` are the tags beyond the session's role and transport, and are empty when there are none;
- `SessionMetricsSink.record` is called synchronously where the event happens, which may be the reader thread. It
  must be cheap and must not block. A `RuntimeException` it throws is ignored.

Reported this way:

- `InvocationRuntime`: invocation queue waits and large-message aborts;
- `PipeManager`: pipe refusals (`local` or `remote`), pipe resets (too large or invalid item) and pipe credit
  waits.

**Layer snapshots.** Each takes `nowNanos` from `NexusSession.nanoTime()` where it measures ages:

| Member | Returns |
|---|---|
| `InvocationRuntime.snapshot(nowNanos)` | `NexusInvocationSnapshot` (C# `NexusInvocationSnapshot`): queued and executing invocations and the oldest queue wait, per pool (normal and separate), the pool sizes, the inbound credits, the outbound credits available, the calls waiting for credit and the calls awaiting a result. `creditDrops` is always 0 on a client |
| `InvocationRuntime.budgetSnapshot()` | `NexusBudgetSnapshot`: the session budget's limit and reserved bytes |
| `InvocationRuntime.largeMessageSnapshots(nowNanos)` | One `NexusLargeMessageSnapshot` per large argument or result in transfer, received ones first: direction, kind, invocation id, total and transferred bytes, time since the last progress, and whether it is being aborted |
| `PipeManager.snapshots(nowNanos)` | One `NexusPipeSnapshot` per pipe holding a receive window, by id |

`Diagnostics.snapshot(client)` combines these with the session readings into a `NexusSessionSnapshot` whose fields
match the C# record (checked against `tests/golden/snapshot-fields.json`). It returns null when the client has no
session. The values are read one at a time, so they are not an atomic picture.

**`NexusMetrics` behavior.**

- **Tracking.** `track(client)` starts reporting a client, and tracking one twice is a no-op. The metrics layer
  registers one `SessionObserver` while it tracks any client, and matches a started session to a tracked client
  through `NexusSession.owner()`.
- **Session lifetime.** Sessions are counted from the transport connection (the session start) until they end, the
  handshake included. `nexnet.session.handshaking` counts the sessions not yet established. A session that already
  exists when its client is tracked is counted from then on, but its duration still runs from its transport
  connection.
- **Event order.** When a session ends, `nexnet.session.duration` is recorded first, tagged with the disconnect
  reason and measured from the transport connection. Then `nexnet.session.disconnects` is added, also tagged
  `nexnet.session.phase`: `handshake` when the session was never established, otherwise `established`. This is
  the C# order.
- **Event instruments.** The session's sink forwards to every `NexusMetricsListener`: counters to `add`, and
  histograms to `record`.
- **Observable instruments.** `collect(MeasurementObserver)` reads them from the live sessions, so the send and
  receive paths pay nothing for metrics. `nexnet.io.frames` and `nexnet.io.bytes` stay monotonic: the totals of
  ended sessions and of untracked clients are kept.
- **Server-only instruments.** `nexnet.connection.rejections` and `nexnet.invocation.busy` are server-only, and a
  client never records `nexnet.invocation.credit_drops`.
- **Listeners** are called synchronously where the event happens, which may be the reader thread. A listener that
  throws is ignored.
