# NexNet Inspector

NexNet Inspector shows what a NexNet connection sends and receives. Every transport read and write is captured with its timing. The bytes are split into frames, frames are reassembled into messages, and message bodies are decoded with the nexus schema into method calls, pipe data, channel items and collection updates.

It has three parts:

| Part | Package / project | What it does |
|------|-------------------|--------------|
| Tap | `NexNet.Inspector.Tap` | Opt-in capture in your server or client: a recorder and an inspector endpoint that viewers connect to. |
| `nexnet-tap` | `NexNet.Inspector.Cli` | Command line: list live sessions, record them to `.nntl` files, summarise and repair recordings; tail, query, check and serve them to LLM agents as condensed text ([LLM & automation](#llm--automation)). |
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

To record a test's traffic and assert on it, use the test helper ([In tests](#in-tests)).

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
nexnet-tap summary|errors|pipes|collections|schema <file.nntl> [options]
nexnet-tap show|trace <file.nntl> <id> [options]
nexnet-tap around <file.nntl> <t> [±w] [options]
nexnet-tap slow <file.nntl> [--over <d>] [options]
nexnet-tap diff <a.nntl> <b.nntl> [--top <n>]
nexnet-tap export <file.nntl> --out <section.nntl> [--from <t>] [--to <t>]
nexnet-tap findings <file.nntl> [--session <id|first|all>] [--slow <duration>] [--json]
nexnet-tap mcp [--token <token>]
nexnet-tap expect <file.nntl | endpoint> [--no-gaps] [--no-errors] [--p99 '<method><<d>,…'] [--max-findings <limit>=<n>,…]
nexnet-tap help [command]
nexnet-tap help format
nexnet-tap --version
```

Endpoint addresses are `tcp://host:port`, `tls://host:port` or `uds:/path`. For TLS, `--insecure` accepts a self-signed certificate and `--server-name` sets the name the certificate must carry. `--connect-timeout` defaults to 10 s.

The commands that read captures as condensed text (`tail`, the queries, `findings`, `expect`, `mcp`) are described in [LLM & automation](#llm--automation).

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
| 2 | Invalid command line, or an `--after` cursor of another session, level, filters or query. |
| 3 | Refused by the server configuration (recording disabled, secrets not included). |
| 4 | Session not found, or none appeared within `--wait` (`findings`: the file has no such session). `tail --until`: the condition never held. For `show` and `trace`, no session holds the ID. |
| 5 | `expect`: an expectation failed. |
| 130 | Interrupted by Ctrl+C before recording started. |

## The app

Start `nexnet-inspector`. The empty window offers **Connect to inspector** (Ctrl+K), **Open recording** (Ctrl+O), your recent endpoints and recordings, and accepts a `.nntl` file dropped on it. Each attached session and each opened recording is a document tab. Ctrl+Tab and Ctrl+Shift+Tab switch tabs in most-recently-used order.

### Connecting

The Connect dialog takes the endpoint address and token, whether it is a server or a client endpoint, the overflow mode (Drop or Block), and whether to request a state snapshot on attach. Once the address is typed, the dialog probes the endpoint and reports its recording policy and schema. After connecting to a server with one session, the dialog attaches to it; with several, the status line asks you to pick one in the title bar's session popover (Esc or Cancel leaves the pick). A session that ended before the attach shows in red, and Connect lists the sessions again.

In a live tab, the title bar shows the endpoint, the session and a REC timer with the time attached. The session popover lists the server's sessions, switches to another one, and runs **Record all sessions** (Ctrl+Shift+R), which writes every session into one `.nntl` file under *Documents/NexNet Inspector/Recordings*. Record all needs `AllowRecording` on the server; otherwise the button is disabled and says why.

A live tab keeps only what arrived since the attach. When the session ends, a strip under the title bar says why and offers **Save .nntl…**, **Attach to another session** and **Discard**; the data stays navigable until you close the tab. Closing a tab, or the window, with unsaved live data asks first: Save .nntl…, Discard or Cancel. **File › Save timeline as .nntl…** (Ctrl+S) saves the active tab at any time.

### Opening a recording

A recording with one session opens directly. Otherwise the open-recording dialog lists its sessions (remote address, transport, start, duration, events); pick one, or use **Compare two sessions…**. When the recording's embedded schema differs from the workspace schema (**Session › Schema › Load workspace schema…**), the dialog shows both hashes and lets you decode with the embedded schema, the workspace schema, or as raw bytes only. Large files open at once and index in the background; views fill in as indexing proceeds.

### Inspect workspace

- **Timeline** (top): transport reads and writes, frames per direction, the handshake, invocations packed into rows, pipes and channels, collections and session control, on a shared time axis. Ctrl+Wheel zooms around the pointer, Wheel pans, Shift+Wheel scrolls the lanes; the overview strip and the ruler above the lanes pick a time range ([Timeline: range, scope and bookmarks](#timeline-range-scope-and-bookmarks)). Click a mark to select it; the selection is traced (see below) in every view. Capture gaps are hatched. Right-click a lane to pin it (P), hide it, or trace the frames in view.
- **Stream panes** (bottom left, one or two side by side with **Single** / split): **Events** (every frame in capture order, with flags; **Filter ▾** shows one direction only, **Follow live** keeps the newest frame in view), **Ladder** (client and server lifelines with an arrow per frame, compact 18 px rows or time-scaled), and **Hex stream** (the transport bytes, 16 per row, with read boundaries, frame annotations, header fields tinted and redacted bytes striped; ▼ IN / ▲ OUT filter the directions). Selecting bytes in the hex view opens a menu to copy them as hex, a C# array or Base64, decode them as MessagePack, UTF-8 or a length-prefixed item, go to an offset (Ctrl+G), bookmark the offset (Ctrl+B, marked with a diamond in the offset gutter) or export them.
- **Inspector** (right): **Decoded** shows the selected frame's header, its transport ops and the decoded message as a tree; hovering a field highlights its bytes in the hex view and the other way round. A message split into chunks shows an assembly bar with one segment per chunk. **Message trace** lists every frame of the traced invocation or pipe in wire order; **Handshake** shows the preambles and greetings.
- Every view follows one selection, one time window and one playhead.

The command palette (Ctrl+Shift+P) searches entities, frames, commands and open documents. Prefixes pick a scope: `>` commands, `#` frames, `@` sessions, `:` time (`:1262.4` jumps to 1,262.4 ms; Ctrl+G opens it with `:`). Tab moves between scopes. **Help & shortcuts** (F1) lists every command with its shortcut and the gestures the views handle. **View › Legend & symbols** (F2, also in the palette) explains every chip, fill and mark the views draw; [Reading the views](#reading-the-views) below has the same legend. Every content menu and the timeline menu also have **Copy for LLM ▸** ([Copy for LLM](#copy-for-llm)).

The theme follows the operating system by default; **View › Theme** switches between Dark, Light and System.

### Timeline: range, scope and bookmarks

Three timeline tools help with long captures: pick a range to look at, narrow the whole document to a scope, and keep views to come back to.

**Range.** The overview strip above the ruler shows the whole session (or the scope) with the visible window boxed. Drag the box's left or right edge handle to move the window's start or end, drag its middle to pan, press outside it to centre the window there, and double-click the strip to fit the whole session. Drag across the ruler to select a time range; releasing zooms to it (a plain click on the ruler still moves the playhead). The same ruler drag works in Compare. Esc cancels any drag and restores the window. The window never gets narrower than 10 µs and never leaves the session, or its scope.

**Scope.** A long capture can be narrowed to a *scope*: **View › Filter to time range…** (Ctrl+Shift+F, also in the palette) sets it from the current view (the default), the selected or traced message, or a start and end typed in ms from the capture's start; **Filter to view** (View menu, palette) and the timeline menu's **Filter to this range** take the current window at once. While a document is scoped, the overview strip and the ruler span only the scope (so it zooms like a short capture), the window cannot leave it, Events and Ladder list only its frames (each keeps its capture number), the Hex stream shows only the transport ops in it, the Message trace dims the steps outside it and marks them ⊘, the palette searches only its entities and frames, **Export section as .nntl…** starts on it, and Compare keeps its aligned axis inside it. Scopes nest: filtering again inside a scope narrows it further. The title bar shows a chip beside the time window, *Scope 1,240.0–1,290.0 ms ✕*, with *‹2* when two scopes are nested: click it (or press Alt+Left, **View › Back to previous scope**) to return to the previous scope and the view you had before it, or click ✕ to clear every scope. Each document keeps its own scopes while you switch tabs; scopes are not saved with a recording. On a live session a scope stays where it is while data keeps arriving after it, and following the live edge is paused until the scope is cleared.

**Bookmarks.** A bookmark keeps a view to come back to: its time window and, when the document is scoped, its scope.

- **Add**: **Ctrl+Shift+B** (**View › Bookmark current view** in the palette) adds the current view and opens the title bar's ★ list with the new name selected for editing. It defaults to the range (*1,240.0 – 1,290.0 ms*): type a name and press Enter, or Esc to keep the default. (Ctrl+B stays the hex view's *bookmark offset*.)
- **List**: the ★ button beside the time window shows how many the recording has and opens the list. Each row shows its Alt+n key, name, range and a *scope* badge when it carries a scope. Click a row (or press Enter on it) to jump there; **F2** renames the focused row in place (Enter keeps, Esc cancels), **Delete** or the row's ✕ deletes it.
- **Jump**: **Alt+1…9** jump to the first nine from anywhere. A jump restores the window and pushes the bookmark's scope again (nested when it lies inside the current scope, otherwise replacing the scopes); a bookmark without a scope keeps the current scope when its window fits inside it, and clears it otherwise. A jump stops following the live edge.
- **Storage**: bookmarks are kept per recording in the app's settings, not in the .nntl file. The key is the file's header block (a fingerprint taken when the capture started), the session and its extent, so a renamed or moved file keeps its bookmarks and every session of a multi-session file has its own. A live session keeps its bookmarks in memory until you save it; saving copies them to the saved file (and the tab that becomes the file shows them). **Export section as .nntl…** offers *Include bookmarks in range*, which copies the bookmarks whose window lies inside the exported range to the new file. A recording keeps at most 50 bookmarks and the app 500 in all; past that, the recordings used least recently lose theirs first.

### Compare workspace

Compare shows two documents side by side: two sessions of one recording, two recordings, or a client-side and a server-side capture of the same connection. Switch the title bar to **Compare** and tick the two tabs to compare, or use **Open in Compare** from the palette (it opens on the entity you selected) or **Compare two sessions…** in the open-recording dialog.

- **Entities** (left): the union of both documents' invocations, pipes and channels, collections, control frames and handshake, grouped by method or pipe, with a Δ where the two differ. Tick a group to give it a row of its own.
- **Aligned sessions**: both documents on one time axis, aligned by **Relative** start, **Wall-clock** time, or **On event** (session ready, or an entity you pick). Drag across the ruler to zoom both documents to a time range, as in the timeline (Esc cancels; a click moves the playhead). Rows show frame density (**Heat**) or entity spans (**Packed**); **Only differences** hides the rows that match. The Δ strip above the rows marks every difference; the selected entity is pinned under its category in both documents.
- **Field diff** and **Hex · A ⇄ B** (bottom): the selected entity's decoded fields side by side, differing rows tinted, and both documents' bytes interleaved row by row with the selected field ringed.

Entities are matched by their wire flow, so a client-side capture and a server-side capture of one connection line up entity for entity. Double-click a region to open that place in the Inspect workspace.

### Reading the views

The app's **Legend & symbols** dialog (logo menu › View, or F2) draws each of these with the views' own colours.

**Frame flags** (chips on events, ladder rows and the inspector):

| Chip | Meaning |
|---|---|
| **T** Transport split | The frame arrived over (or was written by) more than one socket op. |
| **N** NexNet split | NexNet split the message across frames: MessageStart / MessageChunk / Abort, pipe writes, channel items crossing frames. |
| **X** Cancelled | An InvocationCancellation, a Canceled result or an aborted message. |
| **R** Redacted | Bytes replaced by the redaction marker before capture (e.g. the auth token). |
| **…** Incomplete | The frame is still arriving, or the capture cut it off. |

**Direction and transport ops**: ▼ inbound is what the tapped endpoint read, ▲ outbound what it wrote, each in its own colour. In the ladder each frame is an arrow between the lifelines (the remote peer on the left, the tapped endpoint on the right). Op chips name socket ops: `#n` is read *n*, `wN` write *N*, `#a–#b` a frame spanning reads *a* to *b*; a chip outlined in the T colour is an op the frame was split across.

**Badges**: a count badge on a timeline group header (`3 rows · 9`); the Compare difference count; **synced** in a pane header (the pane follows the timeline's window, playhead and selection; *unsynced* when detached); the title bar's **REC** timer (time since attach) and **ALL · n** (Record all, *n* sessions so far); the **scope chip** (violet, *‹n Scope a–b ms ✕*): every view is limited to that time range, ‹n counts nested scopes, a click or Alt+Left goes back and ✕ clears; the **★ bookmarks** button (*★ 3 ▾*, the star violet once the recording has one): the saved views, Ctrl+Shift+B adds, Alt+1…9 jump, the list renames (F2) and deletes.

**Timeline**: the overview strip shows the whole session (or the scope, labelled *scope a – b ms*) with the visible window boxed, a handle on each edge of the box (the one under the pointer turns blue); the ruler shows time in the window, and a drag across it draws the selection band (violet, solid edges) it will zoom to. Lane groups are TRANSPORT, FRAMES, HANDSHAKE, INVOCATIONS, PIPES · CHANNELS, COLLECTIONS and SESSION CONTROL (click a header to fold it). Blocks are reads, writes and frames in their direction's colour, red when cancelled; thin T / N lines mark split frames. Invocation spans run from call to result: the traced span is violet, a cancelled span has a red right edge. A pipe or channel lane shows its lifetime as a line from a green open mark to a red close mark. The trace band (dashed edges) spans the traced message; a hatched gap marks events lost to Drop-mode overflow; the playhead is the current time every view follows. A pinned lane (◆) stays in view at 24 px; lanes never change height on hover.

**Ladder**: a solid trace bracket marks the traced message's frames; a dotted bracket and a dimmed row mark another message's frame interleaved inside the trace. In time-scaled mode the gap above a row grows with the time since the row before.

**Hex stream** fills: preamble (or ping), type byte, body length, post-header (the pipe id, or a chunk's invocation id), body, the selected frame's body, other frames, and no fill where no frame covers the byte yet. A `read #n → …` rule starts each new transport op and `▸ Type #seq` annotates frames starting in the next row. Striped `··` cells are redacted, a violet ring is the selected field, a blue ring the hovered one, and a diamond in the offset gutter a bookmark.

**Inspector assembly bar**: `S` is the MessageStart, `1 … n` the chunks sized by their bytes (the selected frame's is filled), `A` a MessageAbort; a tick in a wider gap means other messages' frames arrived in between. For the handshake, `Pre` and `Greet` with ▼ ▲ are the preambles and greetings. In the Message trace a dimmed step marked ⊘ lies outside the document's scope.

**Channels and pipes**: a typed channel (`Channel<T>`) or a synchronized collection is not a wire concept of its own: its items travel as length-prefixed bytes in `DuplexPipeWrite` frames on a duplex pipe. The views name what rides on the pipe first and the pipe second. ≋ marks a channel, ☰ a collection and ⇄ a raw byte pipe, on timeline lanes ("≋ Channel<LogLine> 36·36 · TailLogs"), ladder targets, hex frame annotations ("▸ DuplexPipeWrite #15,734 ≋ LogLine @EB6BA") and Compare's entities. Events, ladder, Message trace and palette rows keep the true wire type and add a **CH** (channel) or **CO** (collection) chip beside it, and the target counts the items the frame carried ("chan 36·36 LogLine ×2", or "LogLine (part)" for an item that crosses frames). Selecting such a frame opens the inspector on its items, decoded, then the frame header and transport, then a "Channel<LogLine> 36·36" section badged CHANNEL whose first row says which duplex pipe carries it.

**Compare**: file A is blue and file B orange. Heat is frame density of a collapsed group, a packed span an entity's lifetime in an expanded group; a pink mark means the entity differs from its match. The Δ strip marks where matched entities differ, the diff band is the selected match's time range in both files, and the pinned entity gets a row of its own per file.

## LLM & automation

The inspector's machine-facing outputs share one token-condensed text format, specified in [Inspector text format](../internals/inspector-text-format.md) (`nexnet-tap help format` prints a 20-line legend). It has one line per message rather than per byte, stable IDs that every query accepts (`@0x1BCB` a session, `#5` or `#5.2` an invocation, `≋4·4` a pipe, `f18` a frame), repeated traffic folded into counts, and values cut to a width. The same capture always renders the same bytes. `--format ndjson` writes the same records as one JSON object per line with short keys.

| To | Use |
|---|---|
| Read a recording or watch a live session line by line | [`nexnet-tap tail`](#tail) |
| See the shape of a session in about 30 lines | [`summary`](#queries) |
| Ask about one call, pipe or frame, a moment, or a category | [`show`, `trace`, `around`, `errors`, `slow`, `pipes`, `collections`, `schema`](#queries) |
| Compare two recordings, or cut a section out | [`diff`, `export`](#queries) |
| Have detectors list what looks wrong | [`findings`](#findings), `tail --findings` |
| Fail a CI job on traffic | [`expect`](#expect) (exit 5) |
| Let an agent read captures and attach to live sessions | [`nexnet-tap mcp`](#mcp-server) |
| Paste what the app shows into a chat | [Copy for LLM](#copy-for-llm) |
| Assert on a unit test's traffic | [`NexNet.Inspector.Testing`](#in-tests) |

### Token budgets

- **Estimate.** Budgets count tokens as characters (with the newline) over 4, rounded up, and count each character outside ASCII (the glyphs `≋ ☰ ⇄ … ±`) as 3. The estimate is deliberately pessimistic, so a budget is biased low: output tends to stop short of *n* real tokens rather than past it. It is an estimate, not a tokenizer; leave headroom under a hard context limit.
- **Defaults.** On the command line nothing is budgeted unless you ask (`--max-tokens`, `--max-lines`); `summary` stays about 30 lines per session anyway. The MCP query tools default to 8,000 tokens when a call gives neither `max_tokens` nor `max_lines`, `read` returns 200 lines (at most 1,000), and no MCP result exceeds 100,000 characters. Copy for LLM and the test helper's `Summary()` stop at 4,000 tokens.
- **Cursors.** Output cut by a budget ends with `… budget hit, N withheld, --after <cursor>`. Run the same command with `--after <cursor>` (over MCP, the same call with `after`) for the next page: pages never repeat or lose a line, and a cursor made by another session, level, filter or query is refused (exit 2). A live attach has no position to resume from, so its output carries no cursor: record the session and page through the file. An MCP `attach` stream is read by line number instead (`read` with `after=N`).
- **Spending a budget.** Start with `summary` and `findings`, then follow the IDs they cite with `show` and `trace`, or a moment with `around`. Narrow a `tail` before widening it: `--only errors,slow`, `--method`, `--pipe`, `--grep`, `--level summary`; `--sample 1/N` thins a busy session, and `--value-width` and `--depth` shorten values. Keep `--level frames` and `bytes` (one line per frame) for a short `around` window. Prefer `--max-tokens` to `--max-lines` when the reader is a model: lines vary a lot in length.

### tail

```
$ nexnet-tap tail server.nntl --method 'Send*' --max-lines 200
$ nexnet-tap tail tcp://127.0.0.1:7712 --token dev-token --session first --only errors,slow --slow 50ms
$ nexnet-tap tail server.nntl --until 'state=exception|gap' --context 20
```

Prints a recording or a live session as condensed text, one line per message (the [text format](../internals/inspector-text-format.md) the LLM tooling shares): a header per session, then calls with their arguments, outcome and duration, channel items, collection messages, pipe events, gaps and the end, with pings and repeated lines collapsed and values cut to a width. Lines are written and flushed one at a time, so the output can be piped.

- **Source.** A file is replayed session by session in file order (`--session <id|first>` picks one) and the command ends. `--follow` reads a file that is still being written, like `tail -f`, until its index footer arrives. An endpoint (`tcp://`, `tls://`, `uds:`, as the argument or `--endpoint`, with `record`'s connection options) needs `--session <id|first|all>` and is followed until Ctrl+C, `--duration`, or the session closes. `all` records every session (so the server must allow recording) and prefixes each line with its session ID.
- **Output.** `--level summary|messages|frames|bytes`, `--format text|ndjson`, `--collapse on|off`, `--value-width`, `--depth`.
- **Filters.** `--method` (comma-separated globs), `--pipe` (`4.4`, `≋4·4`, or `4` for either side's ID), `--dir in|out`, `--only errors,slow,cancel,gaps`, `--slow <duration>` (threshold, default 100 ms; given alone it keeps slow calls only), `--grep <text>` (case-insensitive). Header and END lines always pass.
- **Budgets.** `--max-lines`, `--max-tokens`, `--sample 1/N` (one line in N), `--duration`. A budget ends with a cursor to resume from ([Token budgets](#token-budgets)).
- **`--until <expr>`** prints nothing until a line meets the expression, then the `--context` lines before it (default 10, after the session header) and that line, and stops. Terms: `method=<glob>`, `state=ok|exception|canceled|unauthorized|refused|pending|oneway`, `error`, `gap`, `end`, `type=<line kind or frame type>`; `,` means and, `|` means or. It exits 4 when the condition never held.
- **Stopping.** Ctrl+C or `--duration` ends with `… stopped: interrupted, N lines, --after <cursor>`. Files only: a live attach has no position to resume, so its stop lines carry no cursor; record it and tail the file to page through it.

### Queries

The query commands answer one question about a session in a few token-condensed lines, in the text format the inspector's LLM tooling shares (`nexnet-tap help format` prints its legend; the full spec is [inspector-text-format](../internals/inspector-text-format.md)). Each reads a recording (every session, or `--session <id>`; the header's `@0x1BCB` form works too), or a live session: give `--endpoint` and `--session <id|first>` instead of a file, and the command attaches for `--duration` (default 2s) and queries what arrived.

```
$ nexnet-tap summary chat.nntl
$ nexnet-tap show chat.nntl 4 --bytes
$ nexnet-tap trace chat.nntl 4
$ nexnet-tap around chat.nntl +27.5 ±5
$ nexnet-tap summary --endpoint tcp://127.0.0.1:7712 --session first --duration 5s
$ nexnet-tap diff before.nntl after.nntl
$ nexnet-tap export chat.nntl --from +20 --to +60 --out section.nntl
```

| Command | What it prints |
|---------|----------------|
| `summary` | About 30 lines per session: totals, handshake, calls per method with p50/p99/max, errors, cancellations, pipes, the largest messages, window stalls, gaps, what was still open at the end, and the disconnect reason. |
| `show <id>` | One invocation, pipe or frame: its line, then a YAML-like tree (an invocation's arguments and result decoded field by field, its frames and pipe). `--bytes` adds each field's byte range in its message, and a frame's hex. |
| `trace <id>` | The frames that carried each of the entity's messages and the bytes each carried, the transport reads or writes they arrived in, and the frames interleaved with them. |
| `around <t> [±w]` | What happened within `w` (default ±25ms) of `t`, in ms since the session's start as the lines show it. `--level frames` shows frames. |
| `errors` | Calls that ended in an exception, unauthorized or refused, refused pipes, bad frames, and a disconnect that was not graceful. |
| `slow --over <d>` | Calls that took at least `--over` (default 100ms), slowest first. |
| `pipes` | One line per pipe: what it carries, the call that opened it, state, bytes and runs per direction, windows, items. |
| `collections [--contents]` | Each synchronized collection's version and messages; `--contents` lists its items as of the end of the capture. |
| `schema` | Method signatures with their wire IDs, collections, and the declared types they use. |
| `diff a b` | Two recordings compared as the Compare workspace does, worst difference first, then the field diffs of the `--top` (default 5) worst entities. |
| `export a` | A time section (`--from`, `--to`) of one session as a recording that opens on its own. A live export needs the server to allow recording. |

IDs are the ones the lines show: `#5` (or `5`; quote `'#5'` in a shell, where `#` starts a comment), `5.2` (the second invocation that reused wire ID 5), `≋4·4` or `p4` (a pipe), `f18` (a frame). `--format ndjson` writes one JSON object per line. `--max-lines`, `--max-tokens` and `--after` page the output ([Token budgets](#token-budgets)). `--value-width` and `--depth` change how much of each value shows.

### findings

```
$ nexnet-tap findings upload.nntl
@0x3078183DB server tcp 127.0.0.1:60700 schema FindingsServerNexus#60c18e57 t0=2026-11-27T01:00:02.597Z
+32.7 ! warn window-stall ⇄1·1 f12 < writes waited 106.8ms for a window update after using the whole 16.0KB window
+32.7 ! warn window-stall ⇄1·1 < stalled 2× waiting for window updates, 217.4ms in total, longest 110.6ms
findings 2: error 0 warn 2 info 0
```

Runs rule-based detectors over each session and prints its header line, one `!` line per finding, most severe first (`error`, `warn`, `info`, then by time), and a tally. A line reads `<time> ! <severity> <kind> <IDs> <message>`; the IDs are the text format's stable IDs (`#5` an invocation, `≋4·4` a pipe, `f18` a frame, `@0x1BCB` a session), the same ones the inspector's other text tooling accepts. `--session` picks one session (an ID from `info`, or `first`), `--slow` sets the slow-call threshold (default 1 s), `--json` writes ndjson (`k` = `find`).

| Kind | Severity | What it means |
|------|----------|---------------|
| `slow` | warn | A call over `--slow`, or a p99 outlier: longer than every earlier call of its method and at least 10× their median (8 calls needed; methods with too few are judged at the end against all their calls). Pipe and channel methods are not judged. |
| `cancel-race` | warn / info | The caller canceled, but the result (ok or exception) still came: the caller discarded it. Info when the cancellation was sent after the result. |
| `orphan` | error / info | A result, or large-message chunks, whose request was not captured. Info when the capture attached mid-stream or a gap dropped the request. |
| `window-stall` | warn | A pipe writer used its whole credit window and waited at least 50 ms for a window update before writing more; a second line totals repeated stalls. |
| `credit-stall` | warn | A caller used all the invocation credits its peer allows and waited at least 50 ms for credit back. |
| `large` | info / warn | An argument, result or channel item of 1 MiB or more; warn at 80 % of the receiver's `MaxMessageSize` or when the receiver aborted it (`TooLarge`, `Busy`). |
| `chunk-storm` | info | 128 or more MessageChunk frames within one second. |
| `pending` | warn / info | A call with no result when the session ended (info when the capture ended first, or a gap may have dropped the result). |
| `pipe-open` | warn / info | A pipe (not a collection's) that both sides never completed. |
| `protocol` | error / info | Bytes that do not frame, a body that does not decode, a handshake that never completed. Info for frames cut off by a gap or by the end of the capture, and for bytes read while resynchronising. |
| `disconnect` | error / warn / info | A close other than graceful or a shutdown (error for `ProtocolError`, `FlowControl`, `Stalled`); info when the recording was cut off. |
| `schema` | warn / error | The recorded schema differs from the workspace schema or does not read; error when the peers disconnected over a nexus mismatch. |
| `gap` | warn | Ops the capture dropped (Drop overflow), citing the frame before them. |
| `ping-rtt` | warn | A ping answered after 500 ms, or 10× the session's median round trip (at least 50 ms). |
| `reconnect-loop` | warn | Across the sessions of a file: three or more reconnections from one peer (a session starting within 10 s of a failed one, or with a reconnection greeting). |

Each kind lists at most 20 findings per session; one more `info` line counts the rest. The detectors are in the core library (`FindingsEngine`), and run incrementally on a live session too: each update returns what was detected since the last. `nexnet-tap tail --findings` prints them inline, as they are detected, after the lines that revealed them (with the session tag when tailing every session); these lines are extra and do not count against `--max-lines` or `--max-tokens`.

### expect

```
$ nexnet-tap expect chat-client.nntl --no-gaps --no-errors --p99 'Send*<50ms,Join<=1s' --max-findings high=0,warn=2
@0x1 client tcp 127.0.0.1:49943 schema ChatClientNexus#9a68b8ec t0=2026-11-23T21:41:51.833Z
findings none

expect ok no-gaps: gaps 0
expect ok no-errors: errors 0
expect ok p99 Send*<50ms: p99 26.1ms max 26.1ms over 5 calls (slowest #4 SendAttachment)
expect ok p99 Join<=1s: p99 0.2ms max 0.2ms over 1 calls (slowest #1 Join)
expect ok max-findings high=0: error 0, at most 0
expect ok max-findings warn=2: warn or worse 0, at most 2
expect pass: 6 of 6 held
```

An assertion mode for CI: it checks a recording (every session, or `--session`'s) or a live session (`expect tcp://… --token …` attaches for `--duration`, default 2 s), prints the findings report (as `findings`), then one `expect ok` or `expect FAIL` line per expectation with what it measured and the IDs that broke it, then the verdict. It exits 0 when every expectation held and **5** when one failed. Expectations (at least one):

- `--no-gaps`: the capture dropped nothing (no Drop-overflow gaps).
- `--no-errors`: what `errors` lists is empty: no call ended in an exception, unauthorized or refused, no frame failed to read, no pipe was refused, every disconnect was graceful.
- `--p99 'Method<bound'`: the p99 duration of the completed calls of each method (a glob: `Send*`, `*`) is under the bound (`<=` allows equal); a comma separates several. Quote it: `<` is a shell redirect. A method with no completed call fails, so a typo cannot pass.
- `--max-findings limit=n`: at most *n* findings of a severity *or worse* (`error`/`high`, `warn`/`medium`, `info`/`low`), of one kind (`slow=0`, `gap=0`, `window-stall=1`, …), or in all (`all=5`).

`--slow` sets the findings' slow-call threshold as for `findings`.

### MCP server

`nexnet-tap mcp` runs a [Model Context Protocol](https://modelcontextprotocol.io) server on stdin and stdout, so an agent such as Claude Code can read captures and watch live sessions itself. It is the same Native AOT binary, with tools only (no resources or prompts), and it targets MCP revision **2026-07-28**, the current stable one at the time of writing (`server/discover`; every request names its revision in `_meta`). Clients of the earlier initialize-based revisions (2025-11-25, 2025-06-18, 2025-03-26) are served too: `initialize` negotiates one of them, and `ping` works. Messages are newline-delimited JSON-RPC 2.0, one per line; diagnostics go to stderr only. The server runs until stdin closes; it then answers the calls still running, detaches open streams and stops recordings (each gets its index footer).

A project's `.mcp.json` for Claude Code (with `nexnet-tap` on the `PATH`, or an absolute `command`):

```json
{
  "mcpServers": {
    "nexnet-inspector": {
      "type": "stdio",
      "command": "nexnet-tap",
      "args": ["mcp"],
      "env": { "NEXNET_TAP_TOKEN": "${NEXNET_TAP_TOKEN}" }
    }
  }
}
```

The endpoint token comes from the `token` argument of a call, or else from `NEXNET_TAP_TOKEN` in the server's environment (as above, expanded from yours) or `mcp --token`. No result or log line repeats it. Auth tokens inside captures stay redacted unless the inspected server's configuration includes secrets; the tools never ask for them.

| Tool | What it does |
|------|--------------|
| `list_sessions(endpoint, token?)` | The endpoint's role, recording policy and live sessions (`list`). |
| `attach(endpoint, session, …)` | Starts a live tail (`tail`) of a session, `first`, or `all` and returns a `stream_id`. Takes `tail`'s level, format, filters (`method`, `pipe`, `dir`, `only`, `slow`, `grep`), `findings`, `until`/`context` and `duration`. |
| `read(stream_id, after?, max_lines?, wait_ms?)` | The stream's lines from line number `after` (default 0): at most `max_lines` (default 200, hard cap 1000), then `… next: after=N (…)` with the stream's state. `wait_ms` (up to 30 s) waits for a new line. A stream keeps its last 10,000 lines. |
| `detach(stream_id)` | Ends the stream and returns the lines not read yet. |
| `summary`, `show`, `trace`, `around`, `schema` | The queries above, over a `file`, or a live session (`endpoint`, `session`, `duration`). |
| `findings(file, session?, slow?)` | The findings above. |
| `diff(a, b, session?, top?)` | Two recordings compared. |
| `export_section(file \| endpoint, out, from?, to?, force?)` | A time section as its own recording. |
| `record_start(endpoint, session, out, …)` / `record_stop(record_id)` | Records a live session (or all) into a file until stopped; refused, as `record` is, unless the server allows recording. |

Each tool has a JSON Schema for its arguments and returns the text the matching command prints. Queries get a default budget of 8,000 tokens when the call gives neither `max_lines` nor `max_tokens`; a budget line ends with `--after <cursor>`, and the same call with `after` set to that cursor returns the next page. A result is never longer than 100,000 characters. Arguments that do not fit the schema, a missing token, a refused recording or an unknown ID come back as a tool result with `isError: true` and a message that says what to change; protocol mistakes (an unknown tool or method, a line that is not JSON) are JSON-RPC errors. A `notifications/cancelled` for a running call stops it, and the call then gets no response. The file tools read and write only the paths a call names; relative paths resolve against the server's working directory.

### Copy for LLM

The content menus (Events, Ladder, Message trace, the trees) and the timeline's menu have **Copy for LLM ▸** with **Selection** (the selected invocation, pipe or frame, decoded, as `nexnet-tap show` prints it), **Traced message** (the frames and transport ops that carried it, as `trace`), **Current view** (the lines of the time window) and **Scope** (the lines of the document's scope, when it has one). **File › Copy for LLM** (also in the palette) copies the selection, else the traced message, else the current view. Each copies the inspector's text format, the session's header line first, at most about 4,000 tokens; when it is cut, the last line says how many lines were withheld. Paste it into a chat with a model.

### In tests

The `NexNet.Inspector.Testing` project (not packaged; reference it from a test project) records a test's traffic in memory and reads it back as the text format:

```csharp
await using var capture = InspectorCapture.Start<MyServerNexus>(serverConfig, new() { Output = Console.Out });
// ... create the server and clients from serverConfig, run the test ...
await capture.StopAsync();
Console.WriteLine(capture.Summary());          // what nexnet-tap summary prints, per session (about 4k tokens at most)
Assert.That(capture.FindingList()).IsEmpty();  // what nexnet-tap findings reports
```

`Start` takes a `ServerConfig` or a `ClientConfig` (generic with a nexus built with `NexNetEmitInspectorSchema=true` to decode values, or without); call it before the server or client is created. It records with `Block` overflow, so nothing is dropped. `Summary()`, `Findings()`, `FindingList()` and `Text()` work on demand while recording (sessions still connected read as cut off, `END truncated`) and after `StopAsync`; disposing stops the recording and writes the summary and findings to `Output` when set, and the `.nntl` to `SavePath` when set (open it in the app or with `nexnet-tap`).

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
