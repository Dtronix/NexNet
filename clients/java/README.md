# NexNet Java client

The NexNet client runtime for Java 11+ and Android API 24+ (`com.dtronix.nexnet`), plus the JVM language host of the
cross-language test harness. Gradle multi-project, built with JDK 21 and `--release 11`, no runtime dependencies.

**Applications do not depend on these projects.** Nothing is published to Maven. `NexNet.Generator` emits a copy of the
runtime sources (`nexnet-client/src/main/java/**`) into the Java output directory, next to the generated client. See:

- [Multi-language Clients](../../docs/articles/multi-language-clients.md): opt-in, build options, parity, type mapping
- [Java Client](../../docs/articles/java-client.md): getting started
- [Java runtime contract](../../docs/internals/java-runtime-contract.md): the API generated code uses, threading, internals

To try the runtime as a library instead (a local jar), run `clients/java/gradlew -p clients/java :nexnet-client:jar`
and use `nexnet-client/build/libs/nexnet-client-<version>.jar` (it keeps the JSpecify annotations: add
`compileOnly("org.jspecify:jspecify:1.0.0")`); Gradle builds can also `includeBuild` this folder.

## Working on the runtime

Run from the repository root with JDK 21 (no Android SDK needed):

```
clients/java/gradlew -p clients/java check
```

`check` compiles with `-Xlint:all -Werror`, runs the JUnit 5 tests (golden vectors included), builds the javadoc and
checks the Android API-24 signature with Animal Sniffer. `-Pnexnet.testJava=11` runs the tests on Java 11. The tools
are described in [Contributing (Java & JS)](../../docs/articles/contributing-multilang.md).

| Project | Contents |
|---|---|
| `nexnet-client` | The runtime: `client`, `transport`, `session`, `invocation`, `pipes`, `collections`, `reconnect`, `pool`, `serialization`, `metrics`, `diagnostics`, `spi` (the API generated code uses) |
| `interop-host` | The JVM language host the C# test harness drives (`docs/internals/interop-control-protocol.md`) |
| `build-logic` | The `nexnet.java-library` convention plugin (release 11, lint, Animal Sniffer) |

Every file under `nexnet-client/src/main/java` is embedded in `NexNet.Generator`, so a change here changes what
consumers receive on their next build. APIs missing on Android API 24 (`java.time`, `java.util.Base64`,
`java.net.http`, …) fail `check`.

MIT License.
