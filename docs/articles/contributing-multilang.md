# Contributing: Java and JavaScript Clients

NexNet is gaining client-only libraries for Java and JavaScript. Their sources live outside `src/`:

| Path | Contents |
|---|---|
| `clients/js/` | npm workspace for `@dtronix/nexnet` and the Node and browser interop hosts |
| `clients/java/` | Gradle multi-project for `com.dtronix:nexnet-client` and the JVM interop host |

This page lists the tools each kind of contributor needs. Run the toolchain check to see what is installed:

```powershell
pwsh tools/setup/check-toolchain.ps1
```

```bash
bash tools/setup/check-toolchain.sh
```

Both scripts print each tool as found or missing, with its version. They always exit 0, unless you pass `--require` with a comma-separated list of tools. Then a missing or too-old required tool makes the script exit 1:

```bash
bash tools/setup/check-toolchain.sh --require java,node
```

Recognized `--require` names: `dotnet`, `java` (checks both `java` and `javac`), `node`, `npm`, `android` (`ANDROID_HOME`) and `playwright`.

## C#-only contributors

**You need nothing beyond the .NET SDK.** The C# build and tests never invoke Gradle, Java, Node or npm:

```
dotnet build src -c Release
dotnet test src/NexNet.Serialization.Tests -c Release
dotnet test src/NexNet.Generator.Tests -c Release
dotnet test src/NexNet.IntegrationTests -c Release
```

- Cross-language interop test rows are generated only when `NEXNET_TEST_LANGS` is set (see [Environment flags](#environment-flags)). Without it, the only test row is the in-process C# client.
- Generator tests for the Java and JavaScript emitters are text snapshots and need no SDK. Tests that compile the generated code with `javac` or `tsc` report a visible skip when the tool is missing.

## Java

**JDK:** any OpenJDK 21 or later. [Eclipse Temurin](https://adoptium.net/) 21 is recommended.

| Platform | Install |
|---|---|
| Windows | `winget install EclipseAdoptium.Temurin.21.JDK` |
| macOS / Linux | `sdk install java 21-tem` ([SDKMAN!](https://sdkman.io/)) |
| Debian / Ubuntu | `sudo apt install temurin-21-jdk` (after adding the [Adoptium apt repository](https://adoptium.net/installation/linux/)) or `sudo apt install openjdk-21-jdk` |

Make sure `java` and `javac` are on `PATH`, or set `JAVA_HOME`.

**Gradle:** use the committed wrapper only. Do not install Gradle separately.

```
clients/java/gradlew -p clients/java check
```

The libraries compile with `--release 11` and target Android API 24 and later.

**No Android SDK is needed** for core development. `check` runs [Animal Sniffer](https://www.mojohaus.org/animal-sniffer/) against the Android API-24 signature (`net.sf.androidscents.signature:android-api-level-24`), which Gradle downloads from Maven Central.

### Android module only

The Android SDK is needed only for work on `nexnet-client-android` and for reproducing the nightly CI job. That job builds the AAR, runs D8/R8 at `--min-api 24`, and smoke-tests on an API-24 emulator.

1. Install the [Android command-line tools](https://developer.android.com/studio#command-line-tools-only).
2. Set `ANDROID_HOME` to the SDK root.
3. Install the packages:

   ```
   sdkmanager "platforms;android-24" "platforms;android-36" "build-tools;36.0.0" "platform-tools" "emulator" "system-images;android-24;default;x86_64"
   ```

## JavaScript

**Node.js:** version 22 or later. 24 LTS is recommended. npm ships with Node.

```
npm ci --prefix clients/js
npm test --prefix clients/js
npm run typecheck --prefix clients/js
```

**Playwright browsers** are needed for the browser test suites (Vitest browser mode) and the browser interop host:

```
npx playwright install chromium
```

Every PR runs the browser suites in Chromium. To reproduce the nightly parity run, install all three engines:

```
npx playwright install chromium firefox webkit
```

## Environment flags

| Variable | Effect |
|---|---|
| `NEXNET_TEST_LANGS` | Comma-separated client languages to run interop tests against, for example `node,java,browser`. When unset, interop rows are not generated and only the in-process C# client runs. |
| `NEXNET_REQUIRE_SDKS` | Set to `1` to make a missing SDK (`javac`, `tsc`, `node`, …) a test **failure** instead of a visible skip. CI sets it. |

When neither variable is set, nothing under `src/` invokes `gradle`, `java`, `javac`, `node`, `npm` or `npx` during `dotnet build` or `dotnet test`.

Generated interop test-host code is written to `artifacts/interop/<lang>/generated`, which is gitignored.

## Publishing prerequisites

Maintainers who publish releases need the following. Contributors do not.

- **Maven Central:** a [Sonatype Central Portal](https://central.sonatype.com/) account with the `com.dtronix` namespace, verified by a DNS TXT record on `dtronix.com`.
- **Signing:** a GPG signing key, with its public key published to a keyserver. Maven Central requires signed artifacts.
- **npm:** membership with publish rights in the npm organization `@dtronix`.
