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
| Dependencies | None at run time. JSpecify (`org.jspecify:jspecify`) is compile-only |

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
| `com.dtronix.nexnet.pipes` | `NexusDuplexPipe`, `NexusDuplexChannel<T>`, `NexusChannelReader<T>`, `NexusChannelWriter<T>` | K08 |
| `com.dtronix.nexnet.collections` | `NexusList<T>`, `NexusListChange<T>`, `NexusCollectionState` | K09 |
| `com.dtronix.nexnet.reconnect` | `ReconnectionPolicy`, `DefaultReconnectionPolicy` (0/2/10/30 s) | K01 (done); K10 wires it |
| `com.dtronix.nexnet.pool` | `NexusClientPool`, `NexusClientPoolConfig`, `RentedNexusClient` | K10 |

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

- K03 emits `public static final Formatter<X>` constants (`<package>/Formatters.java` or per-type); K02 provides the
  built-ins. Formatters are stateless and thread-safe. Generic types get factories (`BoxFormatter.of(Formatter<T>)`).
- `MsgPackWriter`: `writeNil`, `writeBoolean`, `writeInt(long)`, `writeUInt64(long bits)`, `writeFloat`,
  `writeDouble`, `writeString`, `writeBinary`, `writeArrayHeader`, `writeMapHeader`, `writeExtHeader(type, length)`,
  `writeRaw`, `size`, `toByteArray`.
- `MsgPackReader(byte[], offset, length)`: `peekType`, `tryReadNil`, `readBoolean`, `readLong`, `readInt`,
  `readUInt64`, `readFloat`, `readDouble`, `readString`, `readBinary`, `readArrayHeader`, `readMapHeader`,
  `readExtHeader`, `lastExtType`, `readRaw`, `skip`, `remaining`. Malformed input throws `MsgPackException`.
- K02 finalizes this API (it owns the formatter contract) and may add methods.

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

    // C# INexusList<string> Names { get; }
    public NexusList<String> names() { return list(5, Formatters.STRING); }
}
```

| C# return | Java return | `ProxyBase` method |
|---|---|---|
| `void` | `void` | `invokeVoid(id, args)` |
| `ValueTask` | `CompletableFuture<Void>` | `invokeTask(id, args, token)` |
| `ValueTask<T>` | `CompletableFuture<T>` (boxed) | `invoke(id, args, formatter, token)` |
| pipe parameter | `CompletableFuture<Void>` (sent) | `invokePipe(id, pipe, args)` |
| channel parameter | `CompletableFuture<Void>` (sent) | `invokeChannel(id, channel, args)` |

- `ArgumentWriter` writes the array header (`serializedCount`) and the arguments in `wireIndex` order;
  `ArgumentWriter.NONE` sends an empty argument payload (no serialized arguments).
- `PipeArgumentWriter` receives the runtime-assigned initial pipe id (1–255) and writes it at the pipe or channel's
  wire position. Pipe and channel methods are sent with `IgnoreReturn | DuplexPipe`.
- The token argument is null or `CancellationToken.NONE` when the method takes no token.
- Returned futures complete on the callback executor. `future.cancel(true)` sends `InvocationCancellation`.

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

## 4. User-facing API

- `NexusClient<TProxy>`: `connect()`/`connect(token)`, `tryConnect()`/`tryConnect(token)` (a
  `ConnectionResult`), `disconnect()`, `state()`, `addStateListener`, `disconnected()`, `proxy()`, `isSecure()`,
  `config()`, `createPipe()`, `createChannel(formatter)`, `close()`.
- `ClientConfig`: `webSocket(URI)` (ws/wss), `httpSocket(URI)` (http/https), `tcp(host, port)`,
  `tcpTls(host, port)`; builder options `tls(TlsOptions)`, `authenticate(Supplier<byte[]>)` or
  `authenticateAsync(Supplier<CompletableFuture<byte[]>>)` (mutually exclusive), `callbackExecutor`,
  `invocationExecutor`, `reconnectionPolicy`, and the timeouts (C# defaults: connection 50 s, handshake 15 s,
  receive 30 s, ping 10 s). UDS and QUIC are not available on Java (D4).
- `TlsOptions`: `sslContext`, `trustManager`, `keyManager` (client certificate), `pinCertificateSha256`,
  `hostnameVerification` (on by default), `serverName`.
- Exceptions: `NexusException` (base, unchecked), `NexusTransportException` (with `TransportError`),
  `NexusRemoteInvocationException` (`remoteErrorCode`, `remoteExceptionType`, `remoteMessage`),
  `NexusUnauthorizedException` (a remote invocation exception), `NexusBusyException`,
  `NexusMessageTooLargeException` (`size`, `limit`). Cancellation uses `java.util.concurrent.CancellationException`.

## 5. Behavior both sides rely on

- **Threads.** One reader and one writer thread per connection (`nexnet-<id>-reader`, `nexnet-<id>-writer`). No IO on
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
