# Generator-emitted client runtime (D31)

Owner: G09. Status: design accepted with G09; follow-ups J27 (JavaScript `.d.ts`) and the items in §12.

NexNet publishes only NuGet packages (D30). A project that turns on a Java or JavaScript target must therefore get
everything it needs from the generator: the generated nexus code **and the client runtime itself** (D31). This page
describes how the runtime sources travel inside `NexNet.Generator.dll` and how they reach the output directory.

## 1. Summary of the recommendations

| Topic | Recommendation |
|---|---|
| Source of the runtime | `EmbeddedResource` items in `NexNet.Generator.csproj` that glob the checked-in sources under `clients/`. No npm or Gradle at build time (D27) |
| JavaScript types | **Commit the `tsc`-generated `.d.ts`** in `clients/js`, check in the JS gate that they are current, and embed them (J27). Until then the runtime ships its JSDoc-typed `.js` and TypeScript consumers set `allowJs` |
| JavaScript layout | `<OutputDir>/nexnet-runtime/`: the `src/` tree plus its own `package.json` (`@dtronix/nexnet`, the same conditional exports) and `LICENSE` |
| JavaScript import | `RuntimeImport=package` (default): generated code keeps importing `@dtronix/nexnet`, and the consumer adds the emitted folder as a `file:` dependency. `RuntimeImport=relative`: generated code imports `./nexnet-runtime/index.js`, no install step |
| Java layout | `<OutputDir>/com/dtronix/nexnet/**` beside the generated package, plus a generated `NexusRuntimeVersion.java` and `com/dtronix/nexnet/LICENSE.txt` |
| JSpecify | **Emit no annotations** when the runtime is emitted (`Nullness=none`). `Nullness=jspecify` keeps them, and the consumer adds `org.jspecify:jspecify` (not a NexNet artifact). Do not vendor the annotation sources |
| Option | `EmitRuntime` per `NexNetTarget`, default `true`. Implicitly `false` for the in-repo hosts (`EmitTestScaffolding=true`, or a JavaScript `ModuleName`) |
| Version | The generator's version (lockstep with NuGet) stamps every runtime file. A new generator rewrites every file; files a newer runtime dropped are deleted through the unpack manifest |
| Licence | MIT. One header line per emitted runtime file plus the full `LICENSE` text in the runtime root |

## 2. How runtime sources get into the generator

`NexNet.Generator.csproj` embeds the runtime sources as manifest resources:

| Target | Files | Logical name |
|---|---|---|
| `javascript` | `clients/js/packages/nexnet/src/**/*.js` | `NexNet.Runtime/javascript/<path under src>` |
| `java` | `clients/java/nexnet-client/src/main/java/**/*.java` | `NexNet.Runtime/java/<path under java>` |
| both | `LICENSE` (repository root) | `NexNet.Runtime/LICENSE` |

- The items are plain MSBuild globs of checked-in files. `dotnet build src` never runs npm or Gradle (D27).
- The items are inputs of `CoreCompile`, so editing a runtime file rebuilds the generator, and nothing else does.
- A missing `clients/` folder (a source drop of `src/` only) is a build error, not a generator without a runtime:
  `NexNet.Generator.csproj` checks that both runtime roots exist.
- Gradle generates one Java source, `com/dtronix/nexnet/NexusRuntimeVersion.java`, from the NexNet version. The
  generator writes the same class itself (`VALUE` = the generator version).
- `EmbeddedRuntime` (`src/NexNet.Generator/Runtime/EmbeddedRuntime.cs`) reads the resources once per process, sorts
  them by path (ordinal), normalizes line endings to LF and drops a UTF-8 BOM. The working-tree line endings
  (`core.autocrlf`) therefore never reach the output.

### 2.1 JavaScript type declarations

The JavaScript runtime is plain `.js` with JSDoc (D2); its `.d.ts` come from `tsc --declaration` into `dist/`, which
is not checked in. The generator cannot run `tsc` (D27). The options:

| Option | Verdict |
|---|---|
| **A. Commit the generated `.d.ts`** (`npm run build:types` into a checked-in folder), and a JS-gate test that rebuilds them and fails on a difference. The generator embeds them | **Recommended.** Same bytes on every machine; drift is caught by the JS gate, which already runs for every `clients/js` change |
| B. Generate them in CI only and embed them there | Rejected: a local `dotnet build` would produce a different generator than CI, so output is not deterministic across machines |
| C. Hand-write `.d.ts` | Rejected: about 95 modules; they would drift from the JSDoc the runtime is checked against |

Until J27 lands, the emitted runtime has no `.d.ts`. TypeScript consumers compile with `allowJs` (with `checkJs` off):
`tsc` then reads the types from the runtime's JSDoc. The proof test (§10) does exactly this. JavaScript consumers are
not affected.

## 3. Output layout

### 3.1 JavaScript

```
<OutputDir>/
  ChatServerNexus.js, ChatServerNexus.d.ts, formatters.js, nexnet-support.js, …   generated code (unchanged)
  nexnet-runtime/
    package.json        name @dtronix/nexnet, version, "type": "module", "sideEffects": false, exports (below)
    LICENSE
    index.js  browser.js  node.js  diagnostics.js  client/ … values/       clients/js/packages/nexnet/src/**
```

The emitted `package.json` has the conditional exports of the published layout, pointing at the local files:
`.` (`browser` → `browser.js`, `node` → `node.js`, `default` → `index.js`), `./node` and `./diagnostics`. It also
makes the folder ESM whatever the consumer's own `package.json` says.

How generated code reaches the runtime (`RuntimeImport`):

| Mode | Generated imports | Consumer step | Notes |
|---|---|---|---|
| `package` (default) | `@dtronix/nexnet` (as today) | `"dependencies": { "@dtronix/nexnet": "file:<OutputDir>/nexnet-runtime" }` and `npm install` (no registry), or a bundler alias / `tsconfig` `paths` | Keeps the `node`/`browser` conditions: in Node, the generated `createXClient` gets `NodeNexusClient` with TCP, TLS, UDS and HttpSocket. One runtime instance however many output folders import it |
| `relative` | `./nexnet-runtime/index.js` (`../` per directory level for files in sub-folders) | none | Plain browser ESM and scripts work with no install and no import map. The import is the platform-neutral entry, so the generated factory knows only WebSocket transports; Node code that needs the Node transports calls `NodeNexusClient.create(config, nexus, XBinding)` from `./nexnet-runtime/node.js` |

`package` is the default because it behaves exactly like the published package would (D2's `exports` map), and
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

## 4. Options

Each option is `NexNetTarget` item metadata (`<NexNetTarget Include="java" OutputDir="…" EmitRuntime="false" />`) or a
shorthand property.

| Metadata | Shorthand property | Values | Default |
|---|---|---|---|
| `EmitRuntime` | `NexNetJavaEmitRuntime`, `NexNetJavaScriptEmitRuntime` | `true`, `false` | `true`, except `false` when `EmitTestScaffolding=true` or (JavaScript) `ModuleName` is set |
| `RuntimeImport` (JavaScript) | `NexNetJavaScriptRuntimeImport` | `package`, `relative` | `package` |
| `Nullness` (Java) | `NexNetJavaNullness` | `none`, `jspecify` | `none` when the runtime is emitted, otherwise `jspecify` |

An invalid value is NEXNET045.

**In-repo hosts and tests.** `src/NexNet.TestNexus` writes the code of the language hosts, which build against the
workspace runtime (`clients/java` project dependency, npm workspace). Emitting the runtime there would put a second
copy of `com.dtronix.nexnet` on the JVM host's source path. Both of its targets turn the runtime off implicitly: Java
through `EmitTestScaffolding=true` (test scaffolding exists only for the in-repo hosts), JavaScript through its
`ModuleName` (the consumer names the module the runtime comes from). An explicit `EmitRuntime="false"` in
`NexNet.TestNexus.csproj` is clearer and is a follow-up (§12); the implicit rules then stay as safe defaults.

## 5. JSpecify

The Java runtime annotates nullable types with `org.jspecify.annotations.Nullable` (a `compileOnlyApi` dependency,
K28), and so does generated code. Without the jar the output does not compile. The options:

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
  longer has are in the previous manifest only and are deleted (G04). Generated code and runtime therefore always
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
- the bundle orders all files of a target by path (ordinal), runtime and generated alike (G04).

A Generator test checks that two runs give byte-identical bundles and that no emitted runtime file contains `\r`.

## 9. Incremental builds and cost

- **Generator.** The resources are read once per process and cached; the runtime part of the bundle depends only on
  the target options, so Roslyn's incremental pipeline caches it like the rest of the bundle step.
- **IDE.** Design-time builds emit no bundle (G04), so the runtime never reaches the IDE's compilations.
- **Compiler.** With both targets and the runtime on, `NexNet.Targets.g.cs` grows by about BUNDLE_SIZE of base64
  comments. The compiler parses it as trivia only.
- **Unpack.** `NexNetUnpackTargets` runs only when the bundle changes, and writes only changed files.

## 10. Package size

The embedded runtime adds about DLL_DELTA to `NexNet.Generator.dll` (JS ~JS_SIZE, Java ~JAVA_SIZE, uncompressed);
the `.nupkg` is a zip, so the download grows by roughly NUPKG_DELTA. Resources are loaded only when a target emits the
runtime; a project without `NexNetTarget` never touches them. Compressing the resources (gzip at build time) would save
about NUPKG_DELTA on disk but adds a build step; not worth it now.

## 11. Proof

`src/NexNet.Generator.Tests/Runtime/EmittedRuntimeTests.cs`, SDK-gated as the other compile checks (D27):

- **Java:** runs the real generator with the Java target, unpacks the bundle to a temporary folder, compiles **only**
  those files with `javac --release 11 -Xlint:all -Werror` (no class path: no runtime jar, no JSpecify), then runs a
  program that builds a client through the generated factory and serializes a generated type.
- **JavaScript:** the same for both import modes: `tsc` (`allowJs`, `strict`) type-checks a TypeScript file against
  the generated `.d.ts` and the emitted runtime, and Node imports the generated module and the runtime and serializes
  a value. `relative` needs no `node_modules` at all; `package` links `node_modules/@dtronix/nexnet` to the emitted
  `nexnet-runtime` folder, which is what `npm install file:…` does.
- SDK-free tests check the layout, the options, NEXNET045/047, determinism, the version stamp and the JSpecify rewrite.

## 12. Follow-ups

- **J27:** commit the `tsc`-generated `.d.ts` of the JavaScript runtime with a currency check in the JS gate, embed
  them, and add `types` to the emitted `package.json`.
- **Docs:** user-facing documentation of the options (`llm-usage.md`, `docs/articles/multi-language-clients.md`,
  release notes).
- **TestNexus:** set `EmitRuntime="false"` explicitly on both targets of `src/NexNet.TestNexus`.
- **CI:** a nightly job that builds an Android app from an emitted Java runtime (D26) would also cover `Nullness=none`
  under D8.
