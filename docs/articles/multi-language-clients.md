# Multi-language Clients

> [!NOTE]
> Java and JavaScript client generation is in development. This page describes the opt-in, which is available now:
> it selects what will be generated, but no Java or JavaScript files are written yet.

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
