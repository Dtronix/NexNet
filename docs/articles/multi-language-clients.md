# Multi-language Clients

> [!NOTE]
> Java and JavaScript client generation is in development. The opt-in and the output configuration below are
> available now; the build writes `nexnet-ir.json`, but the Java and JavaScript emitters do not produce files yet.

NexNet servers stay .NET. Clients can be generated for other languages from the same nexus interfaces, so they use the
same method ids, hashes and handshake as the C# client. Nothing changes for a project that does not opt in.

## Choosing languages

`NexusTargets` (namespace `NexNet`) is a flags enum:

| Value | Language |
|---|---|
| `None` (0) | C# only (the default) |
| `Java` (1) | Java (`com.dtronix:nexnet-client`) |
| `JavaScript` (2) | JavaScript (`@dtronix/nexnet`), browsers and Node |

## Exporting a server nexus

Set `Targets` on the **server** nexus attribute. The export is named after the class:

```csharp
[Nexus<IChatServerNexus, IChatClientNexus>(NexusType = NexusType.Server,
    Targets = NexusTargets.Java | NexusTargets.JavaScript)]
public partial class ChatServerNexus
{
    // ...
}
```

`Targets` on a client nexus is an error (NEXNET043): the server nexus describes both sides of the connection.

## Exporting interfaces without a class

`[assembly: NexusClientExport<TServer, TClient>]` exports a client from two interfaces. Use it for an older API
version, or when the server class lives in another assembly:

```csharp
[assembly: NexusClientExport<IChatServerNexusV1, IChatClientNexus>(
    Targets = NexusTargets.JavaScript,
    Name = "ChatV1")]
```

- `Targets` is required. An export with `NexusTargets.None` exports nothing.
- `Name` defaults to the server interface name without its leading `I` (`ChatServerNexusV1`).
- The export uses the same method ids, hashes and handshake as a C# client nexus
  `[Nexus<IChatClientNexus, IChatServerNexusV1>(NexusType = NexusType.Client)]`, so it connects to a server that
  implements a later version of the API (see [Versioning](versioning.md)).
- With no server class, the export does not know which methods require authorization.
- Interfaces with duplicate method names are reported as NEXNET042 and not exported.

## Configuring output

Generated files are written by the build, into directories you choose. Configure each language in the project that
holds the exported nexus (the one referencing `NexNet.Generator`):

```xml
<PropertyGroup>
  <NexNetJavaOutputDir>../clients/java/src/main/java</NexNetJavaOutputDir>
  <NexNetJavaPackage>com.example.chat</NexNetJavaPackage>
  <NexNetJavaScriptOutputDir>../clients/js/src/generated</NexNetJavaScriptOutputDir>
  <NexNetJavaScriptModuleName>chat-client</NexNetJavaScriptModuleName>
</PropertyGroup>
```

| Property | Meaning |
|---|---|
| `NexNetJavaOutputDir` | Java output directory, relative to the project. Enables the Java target |
| `NexNetJavaPackage` | Java package of the generated code. Required for Java (NEXNET045 otherwise) |
| `NexNetJavaScriptOutputDir` | JavaScript output directory, relative to the project. Enables the JavaScript target |
| `NexNetJavaScriptModuleName` | JavaScript module name. Optional |
| `NexNetIrOutputPath` | Where `nexnet-ir.json` is written. Default `obj/nexnet-ir.json` |

The properties are shorthand for `NexNetTarget` items, which you can also write directly. Every metadata other than
`OutputDir` is passed to the generator as an option of that target:

```xml
<ItemGroup>
  <NexNetTarget Include="java" OutputDir="../clients/java/src/main/java" Package="com.example.chat" />
</ItemGroup>
```

A nexus that opts in to a language with no output directory gets NEXNET044 and nothing is generated for that language.

How the files get to disk:

- The generator does no file IO. It adds `NexNet.Targets.g.cs` to the compilation: a file made only of comments that
  carries every generated file (base64, with a sha256 each) and `nexnet-ir.json`. It compiles to nothing.
- After `CoreCompile`, the `NexNetUnpackTargets` MSBuild target (imported from the package's `build/` and
  `buildTransitive/` folders) verifies the hashes and writes the files. Only files whose bytes changed are rewritten,
  each atomically, so an unchanged build leaves timestamps alone and does not trigger downstream Java/JS rebuilds.
- Files written by the previous build that are no longer generated (a removed export or type) are deleted. The list
  is kept in `obj/nexnet-targets.manifest`. `dotnet clean` deletes every file in it.
- To produce the bundle, the build turns on `EmitCompilerGeneratedFiles`, so the C# output of every source generator
  is also written under `obj/<configuration>/<tfm>/generated/`. This is harmless.
- Output is deterministic: the same sources give byte-identical files on every machine.
- Projects with several `TargetFrameworks` write the files from the first one only.
- Design-time (IDE) builds report the NEXNET diagnostics live but write nothing.

Without any `NexNetTarget`, none of this runs and the build is unchanged.

Treat the output directories as generated: the unpack target owns the files it writes, so do not edit them by hand.
If you delete a generated file yourself, rebuild after a change or run `dotnet clean` first to get it back.

## Types with a custom formatter

A type serialized by a user formatter (`[assembly: NexusFormatter<TFormatter, T>]`) has no language-neutral form.
Map it to a hand-written formatter in each target language:

```csharp
using NexNet.Serialization;

[assembly: NexusFormatter<MoneyFormatter, Money>]
[assembly: NexusTargetFormatter<Money>(NexusTargets.Java, "com.example.MoneyFormatter")]
[assembly: NexusTargetFormatter<Money>(NexusTargets.JavaScript, "./money-formatter.js#MoneyFormatter")]
```

The native formatter must write exactly the bytes the C# formatter writes. Declare one mapping per (type, language);
combined flags apply the same name to each language.

NEXNET048 reports an invalid mapping: a `None` or undefined target, an empty formatter name, or a second mapping for the
same type and language.

## Diagnostics

| ID | Severity | Meaning |
|---|---|---|
| NEXNET042 | Error | A `NexusClientExport` interface declares two methods with the same name |
| NEXNET043 | Error | `Targets` is set on a client nexus |
| NEXNET048 | Error | Invalid `NexusTargetFormatter` |

The unpack target reports these build messages:

| Code | Severity | Meaning |
|---|---|---|
| NEXNETB001 | Warning | A `NexNetTarget` is configured but the generator produced no bundle (it is not referenced as an analyzer) |
| NEXNETB002 | Error | The bundle is corrupt, from another version of the build files, or has an invalid path |
| NEXNETB003 | Error | The bundle has files for a target with no `OutputDir` |
| NEXNETB004 | Error | A generated file cannot be read, written or deleted |
| NEXNETB005 | Warning | A `NexNetTarget` is declared twice; the first declaration is used |
