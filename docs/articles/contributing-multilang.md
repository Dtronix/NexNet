# Contributing: Java and JavaScript Clients

NexNet is gaining client-only libraries for Java and JavaScript. Their sources live outside `src/`:

| Path | Contents |
|---|---|
| `clients/js/` | npm workspace for `@dtronix/nexnet` and the Node and browser interop hosts |
| `clients/java/` | Gradle multi-project for `com.dtronix:nexnet-client`, its optional modules and the JVM interop host |

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

### Optional modules

Besides the core runtime (`nexnet-client`), `clients/java` builds four optional modules. Each has its own Gradle project and third-party dependencies (versions in `clients/java/gradle/libs.versions.toml`):

| Module | Adds | Dependencies |
|---|---|---|
| `nexnet-client-android` | `NexusAndroid`, `NexusConnectionKeeper`, `AndroidNexusLogger`, `NexusJavaTime` | AndroidX `lifecycle-process`, `annotation`; the Android Gradle plugin |
| `nexnet-client-kotlin` | Coroutine and `Flow` adapters (`.kt` sources, `explicitApi()`) | `kotlinx-coroutines-core`; the Kotlin Gradle plugin |
| `nexnet-client-okhttp` | `OkHttpWebSocketConnector` | `okhttp` |
| `nexnet-client-micrometer` | `NexusMicrometerBinder` | `micrometer-core` |

`nexnet-client-android` is part of the build only when `ANDROID_HOME` is set (see [Android module only](#android-module-only)); `check` builds and tests the other modules everywhere.

The generator embeds each module's sources (`src/main/java`, or `src/main/kotlin` for Kotlin) and emits a module only when a consumer lists it in `EmitRuntimeModules` ([Java guide](java-client.md#optional-modules)). Only the sources are emitted: a module's resources and manifest (the Android module's permissions) are not, and neither is anything its Gradle build generates. Anything they provide, and any new third-party dependency of a module, is a step for every consumer that emits the module: list it in the Java guide.

## JavaScript

**Node.js:** version 22 or later. 24 LTS is recommended. npm ships with Node.

```
npm ci --prefix clients/js
npm test --prefix clients/js
npm run typecheck --prefix clients/js
```

The runtime's `.d.ts` files are checked in (`packages/nexnet/types/`). After a change to its JSDoc or exports, run `npm run build:types --prefix clients/js` and commit the result: `typecheck` fails while they are out of date.

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

## How the clients reach users

The Java and JavaScript clients are **not published** to npm or Maven Central. NuGet is the only published channel: `NexNet.Generator` embeds the runtime sources under `clients/` and writes them, with the generated code, into the consumer's output directories. `@dtronix/nexnet` and `com.dtronix:nexnet-client` are names inside the repository, not registry packages.

- Releasing needs nothing beyond the NuGet release; there are no npm or Maven credentials or signing keys to set up.
- The JS and Java builds are still built and tested in CI, and their versions move in lockstep with the NuGet packages.
- A change to a runtime source under `clients/` changes what the generator emits, so it ships with the next NuGet release of `NexNet.Generator`. See [the internals page](../internals/generator-emitted-runtime.md).

### Release guards

- The clients are versioned in lockstep with NuGet. `src/NexNet/NexNet.csproj` is the version source: Gradle reads it, and `pwsh .github/scripts/release-guard.ps1 -StampVersion` writes it into the npm workspace.
- `@dtronix/nexnet` is `"private"` and its `prepublishOnly` script fails, so even `npm publish --dry-run` stops. The Java build fails if a signing or Maven Central plugin, or a remote publishing repository, is added (`clients/java/build-logic/src/main/kotlin/NoRemotePublishing.kt`). `nexnet-client` publishes only to a local repository under its `build/` folder, for the consumer test.
- `.github/scripts/release-guard.ps1` checks all of this on every CI run. A release tag also runs the client gates, uploads the `npm pack` tarball and the Java jars as workflow artifacts, and checks that `NexNet.Generator` embeds the current runtime.

The release steps are in [llm-release.md](https://github.com/Dtronix/NexNet/blob/master/llm-release.md).
