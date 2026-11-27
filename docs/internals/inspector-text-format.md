# NexNet Inspector text format

Status: version 1, implemented in `src/NexNet.Inspector.Core/Text/` (`TextRenderer`). This document is the contract
for the token-condensed text that the inspector's LLM tooling prints (`nexnet-tap` queries and `tail`, the MCP
server, "Copy for LLM"); the golden files in `src/NexNet.Inspector.Tests/Golden/Text/` are its reference outputs.

The format renders one captured session (a `CaptureSession`, from a `.nntl` recording or a live attach) as lines a
model can read cheaply: one line per message rather than per byte, stable IDs to ask about, repeated traffic folded
into counts, every value cut to a width, and budgets that stop at a resumable cursor.

## 1. Conventions

- **One record per line.** Text lines end with `\n`. Every line but the header, summary statistics and hex lines
  starts with its time.
- **Time**: `+22.0` is milliseconds since the session's first block (`t0` in the header), one decimal, invariant
  culture. Durations: `15.3ms` below a second, `1.23s` below a minute, `2m05s` after. Spans of collapsed runs use
  the same form.
- **Direction** is seen from the tapped endpoint: `>` it sent (outbound), `<` it received (inbound). Undirected lines
  (header, handshake, collapsed noise, gap, end) carry no arrow.
- **Sizes**: `812B`, `18.4KB`, `1.2MB`, `3.0GB` (1 KB = 1024 B).
- **Stable IDs** (the same on every rendering of the same capture; queries accept them):

  | ID | Names | Notes |
  |---|---|---|
  | `@0x1BCB` | a session | the session id in the recording, hex |
  | `#5`, `#5.2` | an invocation | its wire invocation id; `.n` marks the n-th invocation of the session that reused that id (one-way calls all use id 0: `#0`, `#0.2`, …) |
  | `≋4·4`, `☰1·1`, `⇄3·3` | a pipe | glyph (≋ typed channel, ☰ synchronized collection, ⇄ raw duplex pipe), then the client's and the server's local pipe ids |
  | `f18` | a frame | its 1-based position in the session's capture order (the Events table's number) |

- **Values** are positional, without member names: strings quoted and escaped (`\"`, `\\`, `\n`, `\r`, `\t`,
  `\uXXXX`), numbers, `true`/`false`, `null`, enum member names, dates in ISO 8601. An invocation's arguments are
  separated by `, `; an object's members by spaces inside `{…}` (a top-level item or result shows its members bare);
  sequences `[a, b, …+3]`, maps `{k: v}`, tuples `(a, b)`, binary `bin 4.0KB`, a pipe argument `pipe:4`, bytes the
  schema does not describe `raw 92B`, an undecodable value `?`.
- **Truncation**: `valueWidth` (default 32) caps each scalar's text; the cut ends with `…`. `depth` (default 2) caps
  nesting: a deeper object shows as `{…}`, a deeper sequence as `[n]`, a deeper map as `{n}`. Sequences and maps show
  at most `max(1, valueWidth / 8)` elements, then `…+n`.
- **Invariant output**: no machine-local time zone, culture or path appears; the same capture and options always
  render the same bytes.

## 2. Levels

| Level | What each line is |
|---|---|
| `summary` | a few lines per session: header, totals, handshake, calls per method with p50/p99/max, pipes, gaps, noise counts, end |
| `messages` (default) | invocations, channel items, collection messages, raw pipe data, pipe events, handshake, gaps, end |
| `frames` | one line per frame (pings, credits and window updates still collapse unless collapsing is off) |
| `bytes` | `frames`, each frame followed by its first `byteLimit` (default 32) bytes in hex |

## 3. Line forms

| Kind (ndjson `k`) | Text form | Notes |
|---|---|---|
| header (`hdr`) | `@0x1BCB server tcp 127.0.0.1:5000 schema ChatServerNexus#df237d00 t0=2026-11-23T21:41:51.833Z` | role, transport, remote endpoint (`-` when unknown), the schema export's name and the high 32 bits of the schema hash (`schema none` without one, `(mismatch)` when it differs from the workspace schema), wall-clock start in UTC |
| handshake (`hs`) | `hs ok v1 token=redacted(sha256:f6c4b35f…) id=9063827708` | `ok` once the server greeting arrived, `fail` when the session ended without one, `none (attached mid-stream)` for a capture that joined late; protocol version, the token as recorded (see §7), `reconnect`, `ver=`, the client id the server assigned |
| call (`call`) | `+27.5 <#4 SendAttachment(293.0KB, 5 chunks, T×5) -> ok 300000 3109464606 "chat://…" 26.0ms` | method (or collection, `m12` for an unknown method id, `?` before the request was read), arguments, outcome, result value, duration from the request's first byte to the result's last. A large (chunked) argument or result shows its size, chunk count, transport splits and how far it got (`…`, `aborted`, `truncated`, `orphan`) |
| result (`res`) | `+31.0 >#3 SendMessage -> canceled 24.1ms` | the outcome of a call shown earlier as pending (live rendering only, §6), or a result whose request was not captured |
| cancel (`cancel`) | `+26.1 <#3 cancel` | a cancellation of a call shown as pending (live rendering only) |
| item (`item`) | `+53.9 >≋4·4 LogLine ×12 0 Info "chat.rooms" "…" (…11 more)` | channel items starting in a frame: the item type, the first item's value; consecutive items of the same pipe and direction collapse |
| collection (`coll`) | `+7.3 >☰1·1 Users Insert v2 @1 17975727804` | collection message kind, `v` version, `@` index (`→` to-index of a Move), `n=` total values of a ResetStart, value(s) |
| data (`data`) | `+32.7 <⇄3·3 data ×3 12.0KB (span 10.3ms)` | one write run of a raw pipe (or of a channel whose items cannot be split: `items unavailable`) |
| pipe (`pipe`) | `+53.8 >≋4·4 open LogLine by #0.6` | `open` (with what it carries and the call that opened it), `refused`, `client done` / `server done` (one direction completed), `complete` |
| noise (`noise`) | `+74.3 ping ×14 credit ×2 window ×3 (span 207.4ms)`, single: `+26.1 >ping`, `+0.6 <window ☰1·1`, `<credit +4` | pings, invocation credits and pipe window updates between two other lines |
| gap (`gap`) | `+55.4 GAP out 320 ops 52.0KB (Drop)` | ops the capture dropped (Drop overflow mode); what was in flight in that direction ends there |
| end (`end`) | `+282.6 END Graceful from client` | the disconnect reason (`stopped` when the recording stopped, `truncated` when the capture ended without a close block) and which side sent the disconnect frame |
| frame (`frame`) | `+27.5 <f34 MessageStart #4 SendAttachment arg 293.0KB 12B N` | frames level: type, the entity it belongs to, a detail (protocol version, token, client id, credits, pipe state, result state, large-message size), length, flags: `T` transport split, `N` NexNet split, `…` incomplete, `X` cancel, `R` redacted, `!` body malformed |
| hex (`hex`) | `  6e0600940005039101` | bytes level: the frame's first bytes, `…+87B` when cut |
| stat (`stat`) | `calls 14 ok 5 cancel 1 oneway 8`, `  <SendMessage ×4 ok 3 cancel 1 p50 0.1ms max 24.1ms` | summary level |
| problem (`prob`) | `+3.0 ! f12 malformed 40B` | a frame that could not be framed, was cut off (`incomplete`), or whose body does not read (`body malformed`) |

At the messages level a preamble, greetings (folded into the handshake line), message chunks and aborts (folded into
their call), result frames of calls already shown, and disconnect frames (the end line) produce no line of their own.

## 4. Collapsing

On by default (`collapse off` shows every event):
- **Noise**: pings, credits and window updates between two other lines become one line with a count per kind and the
  span (a single one keeps its own short line).
- **Runs**: consecutive channel items of one pipe and direction, consecutive collection messages of the same kind,
  and consecutive raw data runs of one pipe and direction become one line `×N`.
- **Repeated identical lines**: consecutive calls whose method, direction, arguments, outcome and result are the same
  become one line `>#5..#16 Join("ada") -> ok … ×12 (span 4.1s)` (the first call's duration).
- A run never extends across a noise line: noise between two items ends the run.

## 5. Budgets and cursors

- `maxLines` and `maxTokens` bound one rendering. Tokens are estimated as characters (plus the newline) over 4,
  rounded up, counting a character outside ASCII as 3: deliberately pessimistic, so a budget errs low.
- A rendering that hits a budget ends with `… budget hit, N withheld, --after <cursor>`: N lines the session holds now
  were not written. The cursor is opaque base64url (no padding) of: format version, level, phase (before the header,
  in the events, done), frames and gap blocks consumed in capture order, records of the event at that position already
  written, and a 64-bit fingerprint of the filters (time window, directions, a match key). A cursor resumes only the
  same session, level and filters; resuming yields no duplicate and no lost line: concatenating the pages equals the
  unbudgeted rendering (the tests check this at every level).

## 6. Live (incremental) rendering

A renderer kept across calls appends what is new on each call: a live tail. A call's lines are those whose events
arrived since the last call; collapsed runs still open at the end of a call are written then (a later call starts a
new run). A call line shows its outcome when the result is known as the line is written; otherwise it shows `-> …`
(`-> … cancel sent` once canceled) and a later call writes a `res` line when the result arrives, or a `cancel` line
when the caller cancels. Rendering a finished session therefore never writes `res` lines, and a cursor resumed on a
live session does not repeat outcomes of calls shown before it. The end line comes once the session finished.

## 7. Redaction

The renderer shows what the capture holds and never more: a token the tap redacted shows as
`token=redacted(sha256:<first 8 hex digits of the marker>…)`, and its bytes at the bytes level are the marker. A
recording made with `InspectorOptions.IncludeSecrets` holds the raw token, and only then does it appear (quoted, cut to
the width, with its length).

## 8. ndjson

`format ndjson` writes one JSON object per record with these short keys (absent when empty):

| Key | Meaning |
|---|---|
| `k` | kind code (§3) |
| `t` | time, ms since t0 (one decimal) |
| `d` | `>` or `<` |
| `id` | stable ID |
| `to` | last call ID of a collapsed run |
| `n` | method, item type, collection, frame type, noise kind, or the end's side |
| `v` | the condensed words (arguments, value, pipe event, detail, header text) |
| `r` | outcome (`ok`, `exception`, `canceled`, `unauthorized`, `refused`, `oneway`, `…`); frame flags; end reason |
| `rv` | result value or exception |
| `ms` | call duration in ms |
| `x` | events collapsed (absent for one) |
| `sp` | span of a collapsed run in ms |
| `b` | bytes covered |
| `f` | frame number of the first frame (as in `f18`) |
| `after` | the cursor (`k` = `budget`, `x` = lines withheld; `k` = `stop`, `x` = lines written) |
| `s` | the session ID (`@0x1BCB`) when one output interleaves several sessions (`nexnet-tap tail --session all`); text lines then start with it |

`nexnet-tap tail` ends a stopped run with `… stopped: <reason>, N lines[, --after <cursor>]` (ndjson `k` = `stop`, `v` =
reason): Ctrl+C (`interrupted`), `duration reached`, `until <expr> matched` / `not met`. A live attach prints no
cursor (its frame positions exist only in that attach).

The JSON is written by System.Text.Json source generation with relaxed escaping, so glyphs stay literal.

## 9. Legend

`nexnet-tap help format` prints `TextLegend.Text` (Core), at most 20 lines summarising §1–§5.
