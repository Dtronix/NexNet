# Generator-emitted client runtime

NexNet publishes only NuGet packages; the Java and JavaScript clients are not published to npm or Maven. A project that turns on a Java or JavaScript target must therefore get
everything it needs from the generator: the generated nexus code **and the client runtime itself**, so a consumer needs no npm or Maven NexNet dependency, and the generated code and the runtime always come from the same generator version. This page
describes how the runtime sources travel inside `NexNet.Generator.dll` and how they reach the output directory.

## 1. Summary of the recommendations

| Topic | Recommendation |
|---|---|
| Source of the runtime | `EmbeddedResource` items in `NexNet.Generator.csproj` that glob the checked-in sources under `clients/`. No npm or Gradle at build time |
| JavaScript types | **Commit the `tsc`-generated `.d.ts`** in `clients/js`, check in the JS gate that they are current, and embed them: each emitted `.js` has its `.d.ts` beside it, so TypeScript consumers need no `allowJs` |
| JavaScript layout | `<OutputDir>/nexnet-runtime/`: the `src/` tree plus its own `package.json` (`@dtronix/nexnet`, the same conditional exports) and `LICENSE` |
| JavaScript import | `RuntimeImport=package` (default): generated code keeps importing `@dtronix/nexnet`, and the consumer adds the emitted folder as a `file:` dependency. `RuntimeImport=relative`: generated code imports `./nexnet-runtime/index.js`, no install step |
| Java layout | `<OutputDir>/com/dtronix/nexnet/**` beside the generated package, plus a generated `NexusRuntimeVersion.java` and `com/dtronix/nexnet/LICENSE.txt` |
| JSpecify | **Emit no annotations** when the runtime is emitted (`Nullness=none`). `Nullness=jspecify` keeps them, and the consumer adds `org.jspecify:jspecify` (not a NexNet artifact). Do not vendor the annotation sources |
| Option | `EmitRuntime` per `NexNetTarget`, default `true`. Implicitly `false` for the in-repo hosts (`EmitTestScaffolding=true`, or a JavaScript `ModuleName`) |
| Optional Java modules | Opt-in per module: `EmitRuntimeModules="kotlin;okhttp;micrometer;android"` on the Java target emits each module's sources beside the core runtime. Their third-party libraries are the consumer's (§3.3) |
| Version | The generator's version (lockstep with NuGet) stamps every runtime file. A new generator rewrites every file; files a newer runtime dropped are deleted through the unpack manifest |
| Licence | MIT. One header line per emitted runtime file plus the full `LICENSE` text in the runtime root |

## 2. How runtime sources get into the generator

`NexNet.Generator.csproj` embeds the runtime sources as manifest resources:

| Target | Files | Logical name |
|---|---|---|
| `javascript` | `clients/js/packages/nexnet/src/**/*.js` | `NexNet.Runtime/javascript/<path under src>` |
| `java` | `clients/java/nexnet-client/src/main/java/**/*.java` | `NexNet.Runtime/java/<path under java>` |
| both | `LICENSE` (repository root) | `NexNet.Runtime/LICENSE` |
| `java`, module `<m>` | `clients/java/nexnet-client-<m>/src/main/java/**/*.java` (`kotlin`: `src/main/kotlin/**/*.kt`) | `NexNet.Runtime/java-<m>/<path>` |

- The items are plain MSBuild globs of checked-in files. `dotnet build src` never runs npm or Gradle.
- The items are inputs of `CoreCompile`, so editing a runtime file rebuilds the generator, and nothing else does.
- A missing `clients/` folder (a source drop of `src/` only) is a build error, not a generator without a runtime:
  `NexNet.Generator.csproj` checks that both runtime roots and the four module roots exist.
- Gradle generates one Java source, `com/dtronix/nexnet/NexusRuntimeVersion.java`, from the NexNet version. The
  generator writes the same class itself (`VALUE` = the generator version).
- `EmbeddedRuntime` (`src/NexNet.Generator/Runtime/EmbeddedRuntime.cs`) reads the resources once per process, sorts
  them by path (ordinal), normalizes line endings to LF and drops a UTF-8 BOM. The working-tree line endings
  (`core.autocrlf`) therefore never reach the output.

### 2.1 JavaScript type declarations

The JavaScript runtime is plain ESM `.js` with JSDoc, so it runs without a build step; its `.d.ts` come from `tsc --declaration`. The generator
cannot run `tsc`: `dotnet build` never runs Node or npm. The options:

| Option | Verdict |
|---|---|
| **A. Commit the generated `.d.ts`** (`npm run build:types` into a checked-in folder), and a JS-gate test that rebuilds them and fails on a difference. The generator embeds them | **Chosen.** Same bytes on every machine; drift is caught by the JS gate, which already runs for every `clients/js` change |
| B. Generate them in CI only and embed them there | Rejected: a local `dotnet build` would produce a different generator than CI, so output is not deterministic across machines |
| C. Hand-write `.d.ts` | Rejected: about 95 modules; they would drift from the JSDoc the runtime is checked against |

Option A is implemented. `npm run build:types --prefix clients/js` writes the declarations to
`clients/js/packages/nexnet/types/` (checked in), and `npm run typecheck` fails when they differ from a fresh build
(`scripts/check-types.js`). The workspace `package.json` points its `types` conditions at `./types/` and packs that
folder. The generator embeds `types/**` beside `src/**`, so the emitted runtime has each `.d.ts` next to its `.js`,
and the emitted `package.json` carries `types` conditions. TypeScript consumers need no `allowJs`; the proof test
(§11) type-checks with `allowJs` off.

## 3. Output layout

### 3.1 JavaScript

```
<OutputDir>/
  ChatServerNexus.js, ChatServerNexus.d.ts, formatters.js, nexnet-support.js, …   generated code (unchanged)
  nexnet-runtime/
    package.json        name @dtronix/nexnet, version, "type": "module", "sideEffects": false, exports (below)
    LICENSE
    index.js  browser.js  node.js  diagnostics.js  client/ … values/       clients/js/packages/nexnet/src/**
    index.d.ts  browser.d.ts  node.d.ts  …  client/*.d.ts …               clients/js/packages/nexnet/types/**
```

The emitted `package.json` has the conditional exports of the published layout, pointing at the local files:
`.` (`browser` → `browser.js`, `node` → `node.js`, `default` → `index.js`), `./node` and `./diagnostics`, each with a `types`
condition naming the `.d.ts` beside it, and a top-level `types` (`./index.d.ts`). It also
makes the folder ESM whatever the consumer's own `package.json` says.

How generated code reaches the runtime (`RuntimeImport`):

| Mode | Generated imports | Consumer step | Notes |
|---|---|---|---|
| `package` (default) | `@dtronix/nexnet` (as today) | `"dependencies": { "@dtronix/nexnet": "file:<OutputDir>/nexnet-runtime" }` and `npm install` (no registry), or a bundler alias / `tsconfig` `paths` | Keeps the `node`/`browser` conditions: in Node, the generated `createXClient` gets `NodeNexusClient` with TCP, TLS, UDS and HttpSocket. One runtime instance however many output folders import it |
| `relative` | `./nexnet-runtime/index.js` (`../` per directory level for files in sub-folders) | none | Plain browser ESM and scripts work with no install and no import map. The import is the platform-neutral entry, so the generated factory knows only WebSocket transports; Node code that needs the Node transports calls `NodeNexusClient.create(config, nexus, XBinding)` from `./nexnet-runtime/node.js` |

`package` is the default because it behaves exactly like the `@dtronix/nexnet` package (its `exports` map, with separate browser and Node entries), and
because `instanceof` checks need a single runtime copy. A `ModuleName` option still wins over both modes.

### 3.2 Java

```
<OutputDir>/
  com/acme/chat/…                  generated code (the configured Package)
  com/dtronix/nexnet/**.java       clients/java/nexnet-client/src/main/java/**
  com/dtronix/nexnet/NexusRuntimeVersion.java   written by the generator
  com/dtronix/nexnet/LICENSE.txt
```

The runtime keeps its package, `com.dtronix.nexnet`, so code written against the runtime contract
(`docs/internals/java-runtime-contract.md`) and the generated code are unchanged. The consumer adds `<OutputDir>` as a
source root (Gradle `java.srcDir(...)`, Maven `build-helper`, Android `sourceSets.main.java.srcDir`). Build tools
ignore the `.txt` file in a source root. A runtime file and a generated file at the same path are NEXNET047.

### 3.3 Optional Java modules

The optional Java modules are not published either, so the generator carries them too. None is emitted by
default: each needs third-party libraries the generator cannot supply, and one (android) needs an Android build. List
the ones you want in `EmitRuntimeModules` (§4):

```xml
<NexNetTarget Include="java" OutputDir="generated/java" Package="com.acme.chat" EmitRuntimeModules="kotlin;okhttp" />
<!-- or -->
<NexNetJavaEmitRuntimeModules>kotlin;okhttp</NexNetJavaEmitRuntimeModules>
```

Each module keeps its package, beside the core runtime in the same source root:

```
<OutputDir>/com/dtronix/nexnet/kotlin/*.kt       NexusCoroutines.kt, NexusFlows.kt
<OutputDir>/com/dtronix/nexnet/okhttp/*.java     OkHttpWebSocketConnector, OkHttpWebSocketTransport
<OutputDir>/com/dtronix/nexnet/micrometer/*.java NexusMicrometerBinder
<OutputDir>/com/dtronix/nexnet/android/*.java    NexusAndroid, NexusConnectionKeeper, AndroidNexusLogger, NexusJavaTime
```

What the consumer adds for each module (the versions the modules are built and tested with, from
`clients/java/gradle/libs.versions.toml`; any later compatible version works):

| Module | Third-party dependencies (consumer's build) | Build requirements |
|---|---|---|
| `kotlin` | `org.jetbrains.kotlinx:kotlinx-coroutines-core:1.11.0` (and the Kotlin standard library the Kotlin plugin adds) | The Kotlin Gradle plugin (`org.jetbrains.kotlin.jvm` or `.android`, Kotlin 2.x), which compiles `.kt` files in a Java source root. The sources use explicit visibility and build under `explicitApi()` |
| `okhttp` | `com.squareup.okhttp3:okhttp:5.5.1` (brings Okio and the Kotlin standard library) | Plain Java 11 |
| `micrometer` | `io.micrometer:micrometer-core:1.17.2` | Plain Java 11 |
| `android` | `androidx.lifecycle:lifecycle-process:2.11.0`, `androidx.annotation:annotation:1.11.0` | **An Android build** (Android Gradle plugin, `compileSdk` 36, `minSdk` 24): the sources use `android.*` classes, so a plain JVM project cannot compile them. The app's manifest needs `android.permission.INTERNET` and, for `NexusConnectionKeeper`, `android.permission.ACCESS_NETWORK_STATE` (the AAR's merged manifest would add both; emitted sources bring no manifest). No R8/ProGuard rules are needed (the runtime uses no reflection) |

- The `Nullness` option (§5) applies to the module `.java` sources as to the core runtime; the Kotlin sources have no
  JSpecify annotations.
- Every module file gets the runtime header line (§7) and is covered by the same `LICENSE.txt`. The third-party
  libraries are dependencies, not emitted code, so no NOTICE is needed.
- A module path that collides with a generated file is NEXNET047, as for the core runtime.
- Modules need the emitted core runtime: listing one with `EmitRuntime=false` (or with `EmitTestScaffolding=true`
  and no explicit `EmitRuntime`) is NEXNET045. So is an unknown module name.

## 4. Options

Each option is `NexNetTarget` item metadata (`<NexNetTarget Include="java" OutputDir="…" EmitRuntime="false" />`) or a
shorthand property.

| Metadata | Shorthand property | Values | Default |
|---|---|---|---|
| `EmitRuntime` | `NexNetJavaEmitRuntime`, `NexNetJavaScriptEmitRuntime` | `true`, `false` | `true`, except `false` when `EmitTestScaffolding=true` or (JavaScript) `ModuleName` is set |
| `RuntimeImport` (JavaScript) | `NexNetJavaScriptRuntimeImport` | `package`, `relative` | `package` |
| `Nullness` (Java) | `NexNetJavaNullness` | `none`, `jspecify` | `none` when the runtime is emitted, otherwise `jspecify` |
| `EmitRuntimeModules` (Java) | `NexNetJavaEmitRuntimeModules` | a list of `android`, `kotlin`, `micrometer`, `okhttp`, separated by `;`, `,` or spaces, any case | none (§3.3) |

An invalid value is NEXNET045.

The `.editorconfig` that carries the options to the generator reads `;` as the start of a comment, so the MSBuild
flattening (`NexNetFlattenTargets`) turns every `;` in a value into `,`.

**In-repo hosts and tests.** `src/NexNet.TestNexus` writes the code of the language hosts, which build against the
workspace runtime (`clients/java` project dependency, npm workspace). Emitting the runtime there would put a second
copy of `com.dtronix.nexnet` on the JVM host's source path. Both of its targets turn the runtime off implicitly: Java
through `EmitTestScaffolding=true` (test scaffolding exists only for the in-repo hosts), JavaScript through its
`ModuleName` (the consumer names the module the runtime comes from). `NexNet.TestNexus.csproj` also sets
`EmitRuntime="false"` explicitly on both targets ([#294](https://github.com/Dtronix/NexNet/pull/294)); the implicit rules stay as safe defaults.

## 5. JSpecify

The Java runtime annotates nullable types with `org.jspecify.annotations.Nullable` (a `compileOnlyApi` dependency),
and so does generated code. Without the jar the output does not compile. The options:

| Option | Verdict |
|---|---|
| Vendor the annotation sources (`org/jspecify/annotations/*.java`, Apache-2.0, with NOTICE) | Rejected. Guava 33.4+ and many AndroidX libraries depend on `org.jspecify:jspecify`; an app that has both gets duplicate classes, which Android D8 rejects ("defined multiple times") and JPMS rejects as a split package |
| Vendor them under a NexNet package | Rejected: Kotlin, NullAway and IDEs recognize nullness annotations by fully qualified name, so this buys nothing over no annotations |
| **Emit no annotations** (`Nullness=none`) | **Recommended default** with an emitted runtime. Self-contained, no third-party licence, no class clash |
| Keep them (`Nullness=jspecify`) | Opt-in for Kotlin and NullAway users, who add `compileOnly("org.jspecify:jspecify:1.0.0")` (a public, non-NexNet artifact) |

`Nullness=none` removes, from the runtime and the generated Java alike, every `import org.jspecify.annotations.*;` and
every `@Nullable` type-use annotation (`@Nullable T` → `T`, `byte @Nullable []` → `byte[]`,
`java.util.@Nullable List` → `java.util.List`). The annotations have no run-time effect, so behaviour is identical.

## 6. Version stamp and upgrades

- The version is the generator assembly's `major.minor.build` (`<Version>` of `NexNet.Generator.csproj`, lockstep with
  `NexNet`, the npm package version and `NexusRuntime.VERSION`). No commit hash, so output is stable across commits.
- Every emitted runtime file starts with a header line that carries it (§7); the JavaScript `package.json` has it as
  `version`; Java has it in `NexusRuntimeVersion.VALUE` (`NexusRuntime.VERSION`).
- A generator upgrade changes every header, so the unpack task rewrites every runtime file. Files the new runtime no
  longer has are in the previous manifest only and are deleted. Generated code and runtime therefore always
  come from the same generator: there is no version skew to detect at run time.
- Generated code and the runtime are rewritten only when their bytes change (`NexNetUnpackBundle` compares bytes), so
  an unchanged build does not touch file times and does not trigger a Gradle, Vite or `tsc` rebuild.

## 7. Licensing headers

NexNet is MIT. MIT requires the copyright notice and permission notice in copies of the software, so:

- every emitted runtime file starts with one line:
  `// NexNet client runtime <version>. Generated by NexNet.Generator; do not edit. Copyright (c) 2025 Dtronix. MIT License (see LICENSE).`
  (Java: before `package`; JavaScript: before the module JSDoc);
- the full `LICENSE` text is emitted in the runtime root (`nexnet-runtime/LICENSE`, `com/dtronix/nexnet/LICENSE.txt`);
- no third-party code is emitted (§5), so no NOTICE file is needed.

## 8. Determinism

The runtime output is a pure function of the embedded bytes and the generator version:

- resources are sorted by path (ordinal) and every file is LF with no BOM, whatever the checkout's line endings;
- no timestamps, absolute paths, machine names or commit hashes;
- the bundle orders all files of a target by path (ordinal), runtime and generated alike.

A Generator test checks that two runs give byte-identical bundles and that no emitted runtime file contains `\r`.

## 9. Incremental builds and cost

- **Generator.** The resources are read once per process and cached; the runtime part of the bundle depends only on
  the target options, so Roslyn's incremental pipeline caches it like the rest of the bundle step.
- **IDE.** Design-time builds emit no bundle, so the runtime never reaches the IDE's compilations.
- **Compiler.** With both targets and the runtime on, `NexNet.Targets.g.cs` grows by about 2 MB of base64
  comments. The compiler parses it as trivia only.
- **Unpack.** `NexNetUnpackTargets` runs only when the bundle changes, and writes only changed files.

## 10. Package size

Measured on 0.20.0: the embedded runtime adds about 1.5 MB to `NexNet.Generator.dll` (1.3 MB → 2.8 MB; JavaScript
0.58 MB and Java 0.9 MB of source, uncompressed). The `.nupkg` is a zip, so the download grows by about 0.4 MB (the
package is 0.66 MB with the runtime). Resources are loaded only when a target emits the runtime; a project without
`NexNetTarget` never touches them. Compressing the resources at build time would shrink the DLL by about 1 MB on disk
but adds a build step and a decompression on first use; not worth it now.

## 11. Proof

`src/NexNet.Generator.Tests/Runtime/EmittedRuntimeTests.cs`, SDK-gated as the other compile checks (skipped visibly when the tool is missing, mandatory when `NEXNET_REQUIRE_SDKS=1`):

- **Java:** runs the real generator with the Java target, unpacks the bundle to a temporary folder, compiles **only**
  those files with `javac --release 11 -Xlint:all -Werror` (no class path: no runtime jar, no JSpecify), then runs a
  program that builds a client through the generated factory and serializes a generated type.
- **JavaScript:** the same for both import modes: `tsc` (`strict`, `allowJs` off) type-checks a TypeScript file against
  the generated `.d.ts` and the emitted runtime's `.d.ts`, and Node imports the generated module and the runtime and serializes
  a value. `relative` needs no `node_modules` at all; `package` links `node_modules/@dtronix/nexnet` to the emitted
  `nexnet-runtime` folder, which is what `npm install file:…` does.
- SDK-free tests check the layout, the options, NEXNET045/047, determinism, the version stamp and the JSpecify rewrite,
  and for the optional modules: embedding, opt-in, layout, header, `Nullness` and NEXNET045. A real build of
  the integration sample (`BundleBuildTests`) checks that a `;` list reaches the generator.
- **Optional modules:** the modules need their third-party jars, which only Gradle resolves, and `dotnet test` never runs Gradle, so a Gradle
  consumer build compiles them: `clients/java/nexnet-client/emitted-consumer-test`, run by
  `:nexnet-client:emittedConsumerTest` (part of `gradlew check`). It reads the Java output of
  `src/NexNet.Generator.IntegrationSample` and compiles the core runtime and generated code with nothing on the class
  path, then the `okhttp` and `micrometer` modules (`javac --release 11 -Xlint:all -Werror`) and the `kotlin`
  module (explicit API, warnings as errors) each against the emitted core and its own library of §3.3. The task
  is skipped when the sample was never built. It never builds the `android` module.
- **Android module:** the consumer build includes its `android` project, the core plus the `android` module as
  an Android library (minSdk 24), only with `-Pnexnet.emittedConsumer.android=true`. `ANDROID_HOME` alone does not
  include it: CI runners set it in jobs that have no use for it. `emittedConsumerTest` cannot build it either: it
  replaces the parent build's `-P` properties, so the flag never reaches the nested build, and the Android Gradle
  plugin fails in a nested build ("Included builds are not yet available for this build"). The Android job of
  `clients.yml` runs the consumer build in a separate Gradle invocation instead:
  `clients/java/gradlew -p clients/java/nexnet-client/emitted-consumer-test consumerCheck
  -Pnexnet.emitted.dir=<emitted dir> -Pnexnet.emittedConsumer.android=true`.
- **CI:** the `generator-sdks` job of `clients.yml` builds the sample with
  `NexNetJavaEmitRuntimeModules=kotlin;okhttp;micrometer` and runs `emittedConsumerTest` on every pull request; it
  fails if the task is skipped. The `android` job of `clients.yml` emits every module, including `android`,
  runs `consumerCheck` in the separate invocation above with the Android SDK, and dexes the resulting library
  classes with D8 at `--min-api 24`. It runs on pull requests and pushes that touch `clients/java`, the generator,
  the interop server or the test nexuses, and weekly on master (`weekly.yml`).

## 12. Known limits

- **Android:** the Android job of `clients.yml` compiles the emitted core and `android` module in an Android library
  build (minSdk 24) and dexes them with D8 at `--min-api 24`, which covers `Nullness=none` under D8. Not covered: R8
  shrinking of the emitted form and running it on a device.
- **Android consumer build properties:** the separate Gradle invocation's root project is
  `clients/java/nexnet-client/emitted-consumer-test`, so Gradle does not read `clients/java/gradle.properties`;
  only the user's Gradle home and the command-line flags apply. The Android job passes the dependency download retries
  with `-D`. Another property the Android build comes to depend on there (for example `android.useAndroidX`, or the
  JVM arguments, caching and configuration-cache settings of `clients/java/gradle.properties`) is missing from this
  build, and the failure shows up only in the Android job. Pass it on the command line in `clients.yml`, or give the
  consumer build a `gradle.properties` of its own.
