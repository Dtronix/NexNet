# NexNet Inspector

NexNet Inspector shows what a NexNet connection sends and receives. Every transport read and write is captured with its timing. The bytes are split into frames, frames are reassembled into messages, and message bodies are decoded with the nexus schema into method calls, pipe data, channel items and collection updates.

It has three parts:

| Part | Package / project | What it does |
|------|-------------------|--------------|
| Tap | `NexNet.Inspector.Tap` | Opt-in capture in your server or client: a recorder and an inspector endpoint that viewers connect to. |
| `nexnet-tap` | `NexNet.Inspector.Cli` | Command line: list live sessions, record them to `.nntl` files, summarise and repair recordings. |
| NexNet Inspector | `NexNet.Inspector.App` (`nexnet-inspector`) | Desktop app: attach to a live session or open a recording, inspect it, compare two sessions. |

Captures are stored as `.nntl` recordings (format: [Inspector Recording Format](../internals/nntl-format.md)). Both binaries are published with Native AOT for Windows (win-x64) and Linux (linux-x64).

## Enabling the tap

Nothing is captured unless you install the tap. Install it on the configuration before the server starts or the client connects:

```csharp
using NexNet.Inspector.Tap;

var config = new TcpServerConfig { EndPoint = new IPEndPoint(IPAddress.Any, 7711), Authenticate = true };
var inspector = config.UseInspector<ChatServerNexus>(new InspectorOptions
{
    AllowRecording = true,   // off by default
});
var server = ChatServerNexus.CreateServer(config, () => new ChatServerNexus());
inspector.UseSnapshotsOf(server);    // attach snapshots: open pipes, pending invocations, collection state

await server.StartAsync();

// The endpoint is a separate NexNet server on its own listener. Viewers need the token.
var endpointConfig = new TcpServerConfig { EndPoint = new IPEndPoint(IPAddress.Loopback, 7712) };
await using var endpoint = await inspector.StartEndpointAsync(endpointConfig, "dev-token"u8.ToArray());
```

A client is tapped the same way: `clientConfig.UseInspector<ChatClientNexus>(options)` and `inspector.UseSnapshotsOf(client)`.

The generic overload serves the nexus schema to viewers and embeds it in recordings, so bodies decode without any other file. It needs the nexus to implement `IInspectorSchemaSource`, which the source generator adds when the project sets:

```xml
<PropertyGroup>
  <NexNetEmitInspectorSchema>true</NexNetEmitInspectorSchema>
</PropertyGroup>
```

The non-generic `UseInspector(options)` captures without a schema: frames and transport ops are shown, bodies as raw bytes, and a schema can still be loaded in the app.

### Cost when nobody is watching

Installing the tap wraps each session's transport reads and writes behind one null check. While no viewer or recorder is attached, a session's reads and writes do one volatile read and allocate nothing. Capture costs start when a viewer attaches or a recording runs.

### Options

| `InspectorOptions` | Default | Meaning |
|--------------------|---------|---------|
| `AllowRecording` | `false` | Allows `RecordTo`, a viewer's "Record all" and attaches that record. Viewers see the policy before they try. |
| `IncludeSecrets` | `false` | Streams and records the ClientGreeting authentication token as sent. By default it is replaced by a same-length marker (a SHA-256 prefix of the token) and its bytes are flagged as redacted. |
| `RingCapacity` | 4 MiB | Bytes of each viewer's or recorder's buffer, allocated when it starts (at least 4 KiB). |
| `FlushInterval` | 1 s | How often a busy `RecordTo` recording flushes its stream. It also flushes whenever it goes idle. |

### Recording in process

`RecordTo` writes every session into one `.nntl` stream: the sessions already connected from now on (with a snapshot), and every later session from its first byte, preamble and greetings included.

```csharp
var file = new FileStream("server.nntl", FileMode.Create, FileAccess.Write, FileShare.Read);
var recording = inspector.RecordTo(file, overflow: InspectorOverflow.Block);
// ...
await recording.StopAsync();   // writes the index footer
```

A recording that stops without its footer (a crash, a killed process) can still be read: the reader scans it. `nexnet-tap repair` rebuilds the footer.

### Overflow

Captured ops are copied into the viewer's ring buffer and a background sender drains it, so the inspected connection does not wait on the viewer's network. When the ring is full:

- **Drop** (default): ops that do not fit are counted and dropped. The stream records a gap per direction, and the app draws it as a hatched band. Frames after a gap are found again from the frame headers.
- **Block**: the inspected connection waits until there is room. Nothing is lost, and the inspected session slows down to the viewer's pace.

A viewer picks the mode per attach (the Connect dialog, `nexnet-tap record --overflow`).

### Security

- The endpoint listens on its own address. Bind it to loopback or a private interface, or use TLS (`TlsServerConfig`) or a Unix domain socket.
- The endpoint requires the token you give it. It compares tokens in constant time.
- The endpoint is never tapped itself. `StartEndpointAsync` rejects a configuration that has a tap.
- Recording is off until `AllowRecording` is set. Auth tokens are redacted unless `IncludeSecrets` is set. Treat recordings as sensitive: they hold the payloads of every call.

## nexnet-tap

`nexnet-tap` connects to an inspector endpoint, records sessions, and reads recordings. The token comes from `--token` or the `NEXNET_TAP_TOKEN` environment variable.

```
nexnet-tap list   --endpoint <addr> [connection options]
nexnet-tap record --endpoint <addr> --session <id|first|all> --out <file.nntl> [options]
nexnet-tap info   <file.nntl> [--json] [--messages]
nexnet-tap repair <file.nntl> [--force]
nexnet-tap help [command]
nexnet-tap --version
```

Endpoint addresses are `tcp://host:port`, `tls://host:port` or `uds:/path`. For TLS, `--insecure` accepts a self-signed certificate and `--server-name` sets the name the certificate must carry. `--connect-timeout` defaults to 10 s.

### list

```
$ export NEXNET_TAP_TOKEN=dev-token
$ nexnet-tap list --endpoint tcp://127.0.0.1:7712
```

Prints the endpoint's role (server or client), its recording policy and schema hash, then one line per live session.

### record

```
$ nexnet-tap record --endpoint tcp://127.0.0.1:7712 --session first --out chat.nntl --duration 30s
$ nexnet-tap record --endpoint tcp://127.0.0.1:7712 --session all --out server-all.nntl --overflow block
```

`--session` takes a session ID, `first`, or `all` (every current session, and new ones from their first byte). Recording stops on Ctrl+C, after `--duration`, or when the recorded session closes; the command then waits for the endpoint's closing blocks and index footer. A second Ctrl+C stops at once (run `repair` on the file afterwards). Other options:

| Option | Meaning |
|--------|---------|
| `--overflow drop\|block` | Overflow mode for this recording (default `drop`). |
| `--no-snapshot` | Do not ask for the attach snapshot. |
| `--wait <duration>` | Wait up to this long for a session to appear. |
| `--include-secrets` | Record the auth token as sent. Refused unless the server sets `IncludeSecrets`. |
| `--force` | Overwrite an existing output file. |

Durations take a unit: `250ms`, `30s`, `5m`, `1h`, `1h30m`. A bare number is seconds.

### info

```
$ nexnet-tap info chat.nntl
$ nexnet-tap info chat.nntl --messages
$ nexnet-tap info chat.nntl --json > chat.json
```

Summarises a recording: its header, index and any damage, then per session the times, ops and bytes per direction, frames, gaps, snapshot, schema and close reason. `--messages` also reassembles messages and counts invocations, pipes and channel items. `--json` writes the same summary as JSON.

### repair

```
$ nexnet-tap repair crashed.nntl
```

Rebuilds the index footer of a recording that ended without one, in place. A file whose footer is intact is left untouched unless `--force` is given.

### Exit codes

| Code | Meaning |
|------|---------|
| 0 | Success. For `record`, also when stopped by Ctrl+C or `--duration`, or when the session closed. |
| 1 | Failure: endpoint unreachable, token or TLS refused, I/O error, unreadable file, recording without footer. |
| 2 | Invalid command line. |
| 3 | Refused by the server configuration (recording disabled, secrets not included). |
| 4 | Session not found, or none appeared within `--wait`. |
| 130 | Interrupted by Ctrl+C before recording started. |

## The app

Start `nexnet-inspector`. The empty window offers **Connect to inspector** (Ctrl+K), **Open recording** (Ctrl+O), your recent endpoints and recordings, and accepts a `.nntl` file dropped on it. Each attached session and each opened recording is a document tab. Ctrl+Tab and Ctrl+Shift+Tab switch tabs in most-recently-used order.

### Connecting

The Connect dialog takes the endpoint address and token, whether it is a server or a client endpoint, the overflow mode (Drop or Block), and whether to request a state snapshot on attach. Once the address is typed, the dialog probes the endpoint and reports its recording policy and schema. After connecting to a server, choose a session in the title bar's session popover.

In a live tab, the title bar shows the endpoint, the session and a REC timer with the time attached. The session popover lists the server's sessions, switches to another one, and runs **Record all sessions** (Ctrl+Shift+R), which writes every session into one `.nntl` file under *Documents/NexNet Inspector/Recordings*. Record all needs `AllowRecording` on the server; otherwise the button is disabled and says why.

A live tab keeps only what arrived since the attach. When the session ends, a strip under the title bar says why and offers **Save .nntl…**, **Attach to another session** and **Discard**; the data stays navigable until you close the tab. Closing a tab, or the window, with unsaved live data asks first: Save .nntl…, Discard or Cancel. **File › Save timeline as .nntl…** (Ctrl+S) saves the active tab at any time.

### Opening a recording

A recording with one session opens directly. Otherwise the open-recording dialog lists its sessions (remote address, transport, start, duration, events); pick one, or use **Compare two sessions…**. When the recording's embedded schema differs from the workspace schema (**Session › Schema › Load workspace schema…**), the dialog shows both hashes and lets you decode with the embedded schema, the workspace schema, or as raw bytes only. Large files open at once and index in the background; views fill in as indexing proceeds.

### Inspect workspace

- **Timeline** (top): transport reads and writes, frames per direction, the handshake, invocations packed into rows, pipes and channels, collections and session control, on a shared time axis. Ctrl+Wheel zooms around the pointer, Wheel pans, Shift+Wheel scrolls the lanes, and the overview strip above the ruler moves the window. Click a mark to select it; the selection is traced (see below) in every view. Capture gaps are hatched. Right-click a lane to pin it (P), hide it, or trace the frames in view.
- **Stream panes** (bottom left, one or two side by side with **Single** / split): **Events** (every frame in capture order, with flags; **Filter ▾** shows one direction only, **Follow live** keeps the newest frame in view), **Ladder** (client and server lifelines with an arrow per frame, compact 18 px rows or time-scaled), and **Hex stream** (the transport bytes, 16 per row, with read boundaries, frame annotations, header fields tinted and redacted bytes striped; ▼ IN / ▲ OUT filter the directions). Selecting bytes in the hex view opens a menu to copy them as hex, a C# array or Base64, decode them as MessagePack, UTF-8 or a length-prefixed item, go to an offset (Ctrl+G), bookmark the offset (Ctrl+B, marked with a diamond in the offset gutter) or export them.
- **Inspector** (right): **Decoded** shows the selected frame's header, its transport ops and the decoded message as a tree; hovering a field highlights its bytes in the hex view and the other way round. A message split into chunks shows an assembly bar with one segment per chunk. **Message trace** lists every frame of the traced invocation or pipe in wire order; **Handshake** shows the preambles and greetings.
- Every view follows one selection, one time window and one playhead.

The command palette (Ctrl+Shift+P) searches entities, frames, commands and open documents. Prefixes pick a scope: `>` commands, `#` frames, `@` sessions, `:` time (`:1262.4` jumps to 1,262.4 ms; Ctrl+G opens it with `:`). Tab moves between scopes. **Help & shortcuts** (F1) lists every command with its shortcut and the gestures the views handle.

The theme follows the operating system by default; **View › Theme** switches between Dark, Light and System.

### Compare workspace

Compare shows two documents side by side: two sessions of one recording, two recordings, or a client-side and a server-side capture of the same connection. Switch the title bar to **Compare** and tick the two tabs to compare, or use **Open in Compare** from the palette (it opens on the entity you selected) or **Compare two sessions…** in the open-recording dialog.

- **Entities** (left): the union of both documents' invocations, pipes and channels, collections, control frames and handshake, grouped by method or pipe, with a Δ where the two differ. Tick a group to give it a row of its own.
- **Aligned sessions**: both documents on one time axis, aligned by **Relative** start, **Wall-clock** time, or **On event** (session ready, or an entity you pick). Rows show frame density (**Heat**) or entity spans (**Packed**); **Only differences** hides the rows that match. The Δ strip above the rows marks every difference; the selected entity is pinned under its category in both documents.
- **Field diff** and **Hex · A ⇄ B** (bottom): the selected entity's decoded fields side by side, differing rows tinted, and both documents' bytes interleaved row by row with the selected field ringed.

Entities are matched by their wire flow, so a client-side capture and a server-side capture of one connection line up entity for entity. Double-click a region to open that place in the Inspect workspace.

## Recordings and schemas

A `.nntl` file holds one or more sessions: a header (role, machine, wall-clock start), per session the transport ops in capture order with their timing, gap and snapshot blocks, the embedded schema when the tap had one, and an index footer for fast opening. The format is specified in [Inspector Recording Format](../internals/nntl-format.md); the schema it embeds is the generator's IR ([Generator IR](../internals/nexnet-ir.md), §5).

Decoding uses, in order of preference, the schema embedded in the recording or served by the live endpoint, or the workspace schema you load (a `.nnschema` file). A recording made with an older version of your nexus still decodes with its own embedded schema; methods unknown to the schema in use are shown as raw bytes.

## Native AOT

`nexnet-tap` and `nexnet-inspector` publish with Native AOT and no trimming or AOT warnings:

```
dotnet publish src/NexNet.Inspector.Cli -c Release -r win-x64 -o publish/nexnet-tap
dotnet publish src/NexNet.Inspector.App -c Release -r linux-x64 -o publish/nexnet-inspector
```

- Windows needs the Visual Studio C++ build tools (the publish looks them up with `vswhere`). Linux needs `clang` and `zlib1g-dev`.
- The app's publish folder holds the executable, its native Skia and HarfBuzz libraries (and ANGLE on Windows), and the font licences. Copy the whole folder.
- On Linux the window has no system decorations: the app draws its own title bar and resize edges.
- `nexnet-inspector --headless --self-check` builds the main window off screen, renders it in both themes, checks the fonts and the hex editor, and exits 0 or 1. It needs no display; CI runs it on every published binary.
- The tap library (`NexNet.Inspector.Tap`) is AOT-compatible, so a tapped server can itself be published with Native AOT.

## Known limitations

- The session popover and the title bar show the session's transport (`tcp`) where the design shows the client's language; the tap does not report the client language yet.
- The open-recording dialog does not show the recording's client language, redaction or overflow settings.
- The Field diff compares an entity's decoded values; it has no per-frame rows (frame number, timing from MessageStart, transport ops, body hash) yet.
