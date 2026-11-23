# NexNet Inspector recording format (`.nntl`)

Status: format version 1, implemented in `src/NexNet.Inspector.Core/Format/`. This document is the byte-level
contract; the code is the reference implementation.

A `.nntl` file is a timeline of raw transport traffic captured by the inspector tap. It is built so that:

- a recording that was cut off by a crash or a full disk stays readable up to the last complete block;
- a large file can be scrubbed without loading it: an index footer maps every block to its offset;
- one file can hold many sessions (a server recording every session into one shared stream);
- the writer can target any writable stream, including non-seekable ones (pipes, sockets).

## 1. File layout

```
offset 0                                                                     end
| Header block | block | block | ... | block | Index block | Trailer (16 B) |
```

- The first block is always a **Header** block.
- Any number of content blocks follow, in the order they were appended. Blocks of different sessions interleave.
- When the writer closes normally it appends one **Index** block (the footer) and a 16-byte **trailer** that points
  back at it. A crashed recording simply lacks these.
- All offsets are byte offsets from the first byte of the file (the first byte the writer wrote).

## 2. Block framing

Every block is a 16-byte header followed by a body:

| Offset | Size | Field | Notes |
|---|---|---|---|
| 0 | 4 | sync | `C1 4E 54 4C` (`0xC1` then ASCII `NTL`) |
| 4 | 1 | kind | block kind, see §3 |
| 5 | 3 | reserved | must be `00 00 00` |
| 8 | 4 | length | body length in bytes, u32 little-endian, at most 64 MiB (67 108 864) |
| 12 | 4 | checksum | CRC-32C, u32 little-endian, over header bytes 4..11 followed by the body |
| 16 | length | body | one MessagePack array (§4) |

- **Sync marker.** `0xC1` is the one byte MessagePack never assigns, so the marker cannot appear as a type code in
  a body. It can still appear inside raw binary payloads (recorded traffic is arbitrary); the checksum rejects such
  false candidates during resync.
- **Checksum.** CRC-32C (Castagnoli, reflected polynomial `0x82F63B78`, initial value `0xFFFFFFFF`, final
  complement; check value of `"123456789"` is `0xE3069283`). It covers the kind, reserved and length fields as
  well as the body, so a flipped kind or length is detected. Castagnoli was chosen because x64 (SSE4.2) and arm64
  compute it in hardware; the implementation falls back to a table and needs no extra package.
- **Length limit.** A reader treats a length above 64 MiB, or one that runs past the end of the file, as an invalid
  header. This bounds what a garbled length field can make a reader allocate.
- Framing integers are little-endian; values inside bodies are MessagePack (big-endian) as usual.

## 3. Block kinds

| Kind | Value | Session | Index ticks | Body |
|---|---|---|---|---|
| Header | 1 | — | stopwatch ticks at anchor | §4.1 |
| SessionOpen | 2 | yes | open ticks | §4.2 |
| Schema | 3 | — | 0 | §4.3 |
| TransportOp | 4 | yes | op ticks | §4.4 |
| Gap | 5 | yes | first dropped tick | §4.5 |
| Snapshot | 6 | yes | snapshot ticks | §4.6 |
| SessionClose | 7 | yes | close ticks | §4.7 |
| Index | 8 | — | not indexed | §5 |

"Index ticks" is the timestamp the index stores for the block (§5).

## 4. Block bodies

Each body is one MessagePack array. Integers use NexNet's `MsgPackWriter` canonical (smallest) encoding; "bytes" is
a MessagePack `bin`; "str?" is a `str` or `nil`. Enum fields are a single unsigned integer.

All timestamps are **Stopwatch ticks** of the recording machine (`Stopwatch.GetTimestamp()`). The header carries the
single wall-clock anchor that converts them: `utc = WallClockUtcTicks + (ticks - StopwatchTicksAtAnchor) ×
10 000 000 / StopwatchFrequency` (in `DateTime` ticks).

### 4.1 Header (8 fields)

| # | Field | Type |
|---|---|---|
| 0 | magic | str `"NNTL"` |
| 1 | format version | int (1) |
| 2 | wall clock at anchor | int: UTC `DateTime.Ticks` |
| 3 | stopwatch frequency | int: `Stopwatch.Frequency`, `> 0` |
| 4 | stopwatch ticks at anchor | int |
| 5 | producer | str |
| 6 | producer version | str |
| 7 | host | str? |

### 4.2 SessionOpen (8 fields)

| # | Field | Type |
|---|---|---|
| 0 | session id | int (i64) |
| 1 | role | 0 = server, 1 = client |
| 2 | ticks | int |
| 3 | transport name | str |
| 4 | endpoint name | str? |
| 5 | local endpoint | str? |
| 6 | remote endpoint | str? |
| 7 | schema hash | bytes, or nil when no schema |

The session id is the key every later block of the session carries. Session-scoped blocks may precede their
SessionOpen if the recording started mid-session; readers must not assume one exists.

### 4.3 Schema (2 fields)

| # | Field | Type |
|---|---|---|
| 0 | hash | bytes |
| 1 | schema | bytes: `.nnschema` (MessagePack IR), opaque to this format |

The writer embeds each distinct hash once per file. SessionOpen blocks refer to a schema by hash.

### 4.4 TransportOp (7 fields)

| # | Field | Type |
|---|---|---|
| 0 | session id | int |
| 1 | direction | `TapDirection`: 0 = inbound (read), 1 = outbound (written) |
| 2 | op id | int: per-session sequence number assigned by the tap |
| 3 | ticks | int |
| 4 | flags | bit 0 = redacted; other bits 0 in v1 |
| 5 | redactions | array of 2·n ints: `start₀, length₀, start₁, length₁, …` |
| 6 | bytes | bytes: the raw transport read or write |

A redaction range names bytes of field 6 that were replaced before recording (the ClientGreeting auth token becomes a
same-length marker plus a SHA-256 prefix). Ranges lie within field 6; flag bit 0 is set exactly when the array is
non-empty.

### 4.5 Gap (6 fields)

| # | Field | Type |
|---|---|---|
| 0 | session id | int |
| 1 | direction | `TapDirection` |
| 2 | from ticks | int: first dropped op |
| 3 | to ticks | int: last dropped op |
| 4 | dropped ops | int |
| 5 | dropped bytes | int |

Written when the tap's Drop overflow mode discards ops.

### 4.6 Snapshot (4 fields)

| # | Field | Type |
|---|---|---|
| 0 | session id | int |
| 1 | ticks | int |
| 2 | snapshot version | int (1) |
| 3 | sections | array of section arrays |

Each section is an array `[kind, data]` (at least 2 fields): kind 1 = open pipes, 2 = pending invocations,
3 = collection state; data is bytes holding a MessagePack payload whose layout belongs to the section kind. Readers
skip section kinds they do not know. The section payload layouts are defined by the snapshot producer (I05) and bump
the snapshot version only when an existing layout changes incompatibly.

### 4.7 SessionClose (4 fields)

| # | Field | Type |
|---|---|---|
| 0 | session id | int |
| 1 | ticks | int |
| 2 | close kind | 0 = disconnected, 1 = recording stopped while connected |
| 3 | disconnect reason | NexNet `DisconnectReason` value (0 when recording stopped) |

## 5. Index footer and trailer

### 5.1 Index block (kind 8, 2 fields)

| # | Field | Type |
|---|---|---|
| 0 | index version | int (1) |
| 1 | groups | array of group arrays |

Each group is `[kind, session id, entries]` (at least 3 fields). Header and Schema blocks use session id 0. `entries`
is a `bin` of 16-byte records, one per block, in file order:

| Offset | Size | Field |
|---|---|---|
| 0 | 8 | block offset, i64 little-endian |
| 8 | 8 | index ticks (§3), i64 little-endian |

Groups appear in the order their first block appears in the file; entries within a group are in file order. Every
non-index block of the file is in exactly one group. Because the index is a pure function of the block sequence, a
scan rebuilds a byte-identical footer (`NntlFile.RebuildIndex`).

Entry ticks let a reader find a session's transport ops in a tick range without reading them. Ops of one session are
usually in tick order, but inbound and outbound ops can be queued out of order, so readers binary search a group only
after checking its ticks ascend, and filter otherwise.

### 5.2 Trailer (16 bytes, last bytes of the file)

| Offset | Size | Field |
|---|---|---|
| 0 | 8 | index block offset, i64 little-endian |
| 8 | 4 | CRC-32C of bytes 0..7, u32 little-endian |
| 12 | 4 | end marker `4C 54 4E C1` (`LTN` then `0xC1`) |

The footer is valid only when: the end marker and trailer checksum match; the offset points at a block that passes its
own checks and has kind Index; and that block ends exactly where the trailer starts.

## 6. Reading and recovery

### 6.1 Opening

1. Read the block at offset 0. It must be a valid Header block with magic `"NNTL"` and a supported format version;
   otherwise the stream is not a readable `.nntl` file.
2. Unless a scan was requested, check the trailer and footer (§5.2). If valid, use the stored index; no other block is
   read.
3. Otherwise scan (§6.2) and report the index as rebuilt. The viewer shows a warning banner in that case and decodes
   bodies raw when no schema is present.

### 6.2 Scan and resynchronization

Starting at offset 0, at position `p`:

1. **Valid block.** The header has the sync marker, zero reserved bytes, a length within limits and within the file,
   and the checksum matches. Index it (§5.1) and continue at its end. A block whose checksum matches but whose body
   does not decode is reported as *malformed* and skipped by its length. An Index block is not indexed; if it is
   followed by exactly a trailer that points to it, the scan ends there.
2. **Invalid block.** Search forward from `p + 1` for the sync marker. For each candidate, apply the checks of step 1;
   the first candidate that passes is the resync point `q`. Report `[p, q)` as *corrupt* and continue at `q`.
3. **No valid block before the end.** Report `[p, end)` as a *torn tail* and stop. This is the usual crash case: the
   last block (or the footer) was only partly written. Every block before it is recovered.

Damage reports carry the kind (corrupt, malformed, torn tail), the offset and the length.

### 6.3 Random access

With the index loaded, a block is read by seeking to its offset, reading 16 header bytes and the body, and verifying
the checksum. A block that fails this is skipped by enumerations and reported as unreadable by direct reads, so a
damaged region in a file with a valid footer costs only the blocks inside it.

### 6.4 Rebuilding the index

`NntlFile.RebuildIndex` (behind `nexnet-tap repair`) scans as in §6.2, truncates the file after the last valid
non-index block (dropping a torn tail and any old footer), then appends a fresh Index block and trailer. Corrupt
regions in the middle stay in the file; the new index does not point at them.

## 7. Writing

- The writer appends blocks only; it never seeks. It counts the bytes it has written to know each block's offset, so a
  non-seekable output works.
- Each block is encoded into a reused buffer and handed to the stream in one write.
- All append methods are safe to call concurrently: one lock serializes them, so blocks from many sessions interleave
  but never tear into each other. The tap's per-viewer ring decouples this lock from NexNet's per-frame path.
- On close the writer appends the index block and trailer and flushes. If the stream throws, the writer is faulted
  (the offset of the partly written block is unknown) and later appends throw.

## 8. Versioning rules

- The header's **format version** is the major version. A reader refuses a file whose version is higher than the one
  it supports. It changes only for incompatible changes (framing, checksum, existing field meaning).
- **Compatible additions** do not change the format version:
  - new fields appended to the end of a body array: readers decode the fields they know and skip the rest;
  - new block kinds: readers that do not know a kind index it (session 0, ticks 0) and otherwise ignore it;
  - new snapshot section kinds: skipped by readers that do not know them;
  - new transport op flag bits, only if old readers may safely ignore them.
- The reserved header bytes must stay zero; a non-zero value makes the block invalid, so any use of them needs a new
  format version.
- The index version (§5.1) and the snapshot version (§4.6) version their own payloads. An unknown index version makes
  the footer invalid and the reader falls back to a scan.
