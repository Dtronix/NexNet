# NexStream Protocol Specification

**Version:** 1.0 Draft
**Status:** Pre-Implementation

## Table of Contents

1. [Introduction](#1-introduction)
2. [Architecture Overview](#2-architecture-overview)
3. [Frame Format](#3-frame-format)
4. [Frame Types](#4-frame-types)
5. [Stream Lifecycle](#5-stream-lifecycle)
6. [Position and Synchronization](#6-position-and-synchronization)
7. [Metadata](#7-metadata)
8. [Progress Notifications](#8-progress-notifications)
9. [Error Handling](#9-error-handling)
10. [Concurrency Model](#10-concurrency-model)
11. [Public API](#11-public-api)
12. [Implementation Requirements](#12-implementation-requirements)
13. [Security Considerations](#13-security-considerations)
14. [Future Extensions](#14-future-extensions)

---

## 1. Introduction

### 1.1 Purpose

NexStream is a sub-protocol built on `INexusDuplexPipe` that provides stream semantics with bidirectional data framing. It enables remote stream operations including reading, writing, seeking, and metadata retrieval over NexNet connections.

### 1.2 Scope

This specification covers:
- Frame format and parsing for stream operations
- Stream lifecycle management
- Position synchronization between client and server
- Error handling within streams
- Public API for stream transport

Out of scope:
- Transport-layer concerns (handled by NexNet core)
- Application-level file system semantics
- Encryption (handled by transport)

### 1.3 Terminology

| Term | Definition |
|------|------------|
| **Stream** | A logical bidirectional byte channel within a pipe |
| **Transport** | The `INexusStreamTransport` instance managing stream requests |
| **Resource ID** | Server-interpreted string identifier for the requested resource |
| **Frame** | A single protocol message unit on the pipe |
| **Initiator** | The endpoint that sends an Open request |
| **Provider** | The endpoint that provides the stream in response |

### 1.4 Design Principles

1. **One stream per pipe:** Each `INexusDuplexPipe` hosts exactly one stream at a time
2. **Server authoritative:** Server is authoritative for stream position and state
3. **Explicit operations:** Read and write operations require explicit requests with buffer sizes
4. **Bidirectional origination:** Either client or server can initiate stream requests
5. **Error isolation:** Stream errors do not disconnect the session unless protocol violations occur

---

## 2. Architecture Overview

### 2.1 Protocol Stack

NexStream operates as a layer on top of the existing NexNet pipe infrastructure:

- **Application Layer:** User code interacting with `INexusStream`
- **Stream Layer:** Frame parsing, state management, position tracking
- **Pipe Layer:** `INexusDuplexPipe` providing bidirectional byte transport
- **Session Layer:** NexNet session management, message routing
- **Transport Layer:** TCP, UDS, WebSocket, etc.

### 2.2 Component Responsibilities

**NexusStreamFrameWriter:**
- Serializes frames to raw binary format
- Writes to underlying pipe output
- Handles frame chunking for large payloads

**NexusStreamFrameReader:**
- Parses incoming bytes into frames
- Validates frame structure and sequence
- Routes frames to appropriate handlers

**NexusStreamTransport:**
- Manages stream request/response lifecycle
- Coordinates between initiator and provider roles
- Exposes public API for stream operations

**NexusStream:**
- Represents an active stream instance
- Tracks position, state, and metadata
- Provides read/write/seek operations

### 2.3 Constraints

- Maximum concurrent streams per session: 255 (one per pipe)
- Maximum resource ID length: 2000 characters
- Frame payload size: Independent of protocol 65KB limit (configurable)

---

## 3. Frame Format

### 3.1 Frame Header

All frames use a fixed 5-byte header followed by a variable-length payload:

```
Offset  Size    Field       Description
0       1       Type        Frame type identifier
1       4       Length      Payload length in bytes (little-endian)
5       N       Payload     Frame-specific data
```

Total frame size: 5 + Length bytes

### 3.2 Byte Order

All multi-byte integer fields MUST be encoded in little-endian byte order.

### 3.3 Serialization

All frame payloads use manual binary serialization (no MemoryPack). This provides:
- Minimal overhead for data frames
- Full control over wire format
- Easier debugging of raw bytes
- Predictable frame sizes

### 3.4 String Encoding

Strings within frames are encoded as:
- 2-byte length prefix (ushort, little-endian)
- UTF-8 encoded string bytes

Null strings are represented as length 0xFFFF.

### 3.5 Maximum Payload Size

The maximum payload size is configurable per session and independent of the NexNet protocol's 65KB message limit. Default: 64KB. The frame reader/writer will chunk larger payloads automatically.

---

## 4. Frame Types

### 4.1 Frame Type Registry

| Value | Name | Direction | Category | Description |
|-------|------|-----------|----------|-------------|
| 0x01 | Open | Bidirectional | Request | Request to open a stream |
| 0x02 | OpenResponse | Bidirectional | Response | Open result with metadata |
| 0x03 | Close | Bidirectional | Control | Close the stream |
| 0x04 | Seek | Bidirectional | Request | Request position change |
| 0x05 | SeekResponse | Bidirectional | Response | New position result |
| 0x06 | Flush | Bidirectional | Request | Request flush to storage |
| 0x07 | FlushResponse | Bidirectional | Response | Flush complete |
| 0x08 | GetMetadata | Bidirectional | Request | Request current metadata |
| 0x09 | MetadataResponse | Bidirectional | Response | Metadata payload |
| 0x0A | Read | Bidirectional | Request | Request to read N bytes |
| 0x0B | Write | Bidirectional | Request | Write data to stream |
| 0x0C | WriteResponse | Bidirectional | Response | Write acknowledgment |
| 0x10 | Data | Bidirectional | Data | Binary data chunk |
| 0x11 | DataEnd | Bidirectional | Data | Final chunk marker |
| 0x20 | Progress | Bidirectional | Control | Transfer progress update |
| 0x30 | Error | Bidirectional | Control | Stream error notification |
| 0x40 | Ack | Bidirectional | Control | Sliding window acknowledgment |

### 4.2 Frame Type Ranges

| Range | Purpose |
|-------|---------|
| 0x01-0x0F | Request/Response operations |
| 0x10-0x1F | Data transfer |
| 0x20-0x2F | Progress and notifications |
| 0x30-0x3F | Error handling |
| 0x40-0x4F | Acknowledgment |
| 0x50-0xFF | Reserved for future use |

### 4.3 Open Frame (0x01)

Requests opening a stream for a resource.

**Payload:**
| Offset | Size | Field | Type | Description |
|--------|------|-------|------|-------------|
| 0 | 2+N | ResourceId | string | Resource identifier (max 2000 chars) |
| 2+N | 1 | Access | byte | StreamAccessMode flags |
| 3+N | 1 | Share | byte | StreamShareMode flags |
| 4+N | 8 | ResumePosition | long | Position to resume from (-1 = start fresh) |

**StreamAccessMode:**
| Value | Name | Description |
|-------|------|-------------|
| 0x01 | Read | Read access requested |
| 0x02 | Write | Write access requested |
| 0x03 | ReadWrite | Both read and write access |

**StreamShareMode:**
| Value | Name | Description |
|-------|------|-------------|
| 0x00 | None | Exclusive access |
| 0x01 | Read | Allow concurrent readers |
| 0x02 | Write | Allow concurrent writers |
| 0x03 | ReadWrite | Allow concurrent read/write |

### 4.4 OpenResponse Frame (0x02)

Response to an Open request.

**Payload:**
| Offset | Size | Field | Type | Description |
|--------|------|-------|------|-------------|
| 0 | 1 | Success | bool | True if open succeeded |
| 1 | 4 | ErrorCode | int | Error code if failed (0 = success) |
| 5 | 2+N | ErrorMessage | string? | Error message if failed |
| 5+N | M | Metadata | Metadata | Stream metadata (if success) |

### 4.5 Close Frame (0x03)

Closes the current stream.

**Payload:**
| Offset | Size | Field | Type | Description |
|--------|------|-------|------|-------------|
| 0 | 1 | Graceful | bool | True for graceful close |

### 4.6 Seek Frame (0x04)

Requests a position change.

**Payload:**
| Offset | Size | Field | Type | Description |
|--------|------|-------|------|-------------|
| 0 | 8 | Offset | long | Seek offset |
| 8 | 1 | Origin | byte | SeekOrigin (Begin=0, Current=1, End=2) |

### 4.7 SeekResponse Frame (0x05)

Response to a Seek request.

**Payload:**
| Offset | Size | Field | Type | Description |
|--------|------|-------|------|-------------|
| 0 | 1 | Success | bool | True if seek succeeded |
| 1 | 8 | Position | long | New absolute position |
| 9 | 4 | ErrorCode | int | Error code if failed |

### 4.8 Flush Frame (0x06)

Requests flushing buffered data to storage.

**Payload:** Empty (0 bytes)

### 4.9 FlushResponse Frame (0x07)

Response to a Flush request.

**Payload:**
| Offset | Size | Field | Type | Description |
|--------|------|-------|------|-------------|
| 0 | 1 | Success | bool | True if flush succeeded |
| 1 | 4 | ErrorCode | int | Error code if failed |

### 4.10 GetMetadata Frame (0x08)

Requests current stream metadata.

**Payload:** Empty (0 bytes)

### 4.11 MetadataResponse Frame (0x09)

Response containing stream metadata.

**Payload:** See Section 7 (Metadata).

### 4.12 Read Frame (0x0A)

Requests reading data from the stream.

**Payload:**
| Offset | Size | Field | Type | Description |
|--------|------|-------|------|-------------|
| 0 | 4 | Count | int | Number of bytes to read |

The provider responds with one or more Data frames followed by DataEnd.

### 4.13 Write Frame (0x0B)

Writes data to the stream.

**Payload:**
| Offset | Size | Field | Type | Description |
|--------|------|-------|------|-------------|
| 0 | 4 | Count | int | Total bytes being written |

Immediately followed by one or more Data frames.

### 4.14 WriteResponse Frame (0x0C)

Acknowledgment of write completion.

**Payload:**
| Offset | Size | Field | Type | Description |
|--------|------|-------|------|-------------|
| 0 | 1 | Success | bool | True if write succeeded |
| 1 | 4 | BytesWritten | int | Actual bytes written |
| 5 | 8 | Position | long | New stream position |
| 13 | 4 | ErrorCode | int | Error code if failed |

### 4.15 Data Frame (0x10)

Binary data chunk.

**Payload:**
| Offset | Size | Field | Type | Description |
|--------|------|-------|------|-------------|
| 0 | 4 | Sequence | uint | Sequence number for ordering |
| 4 | N | Data | byte[] | Raw binary data |

Data frames may be compressed. Compression is indicated by setting bit 0 of the frame type (0x10 | 0x80 = 0x90 for compressed data).

### 4.16 DataEnd Frame (0x11)

Marks the end of a data sequence.

**Payload:**
| Offset | Size | Field | Type | Description |
|--------|------|-------|------|-------------|
| 0 | 4 | TotalBytes | int | Total bytes in sequence |
| 4 | 4 | FinalSequence | uint | Last sequence number |

### 4.17 Progress Frame (0x20)

Transfer progress notification.

**Payload:**
| Offset | Size | Field | Type | Description |
|--------|------|-------|------|-------------|
| 0 | 8 | BytesTransferred | long | Total bytes transferred |
| 8 | 8 | TotalBytes | long | Total expected bytes (-1 if unknown) |
| 16 | 8 | ElapsedTicks | long | TimeSpan ticks since stream opened |
| 24 | 8 | BytesPerSecond | double | Current transfer rate |
| 32 | 1 | State | byte | TransferState enum |

**TransferState:**
| Value | Name | Description |
|-------|------|-------------|
| 0 | Active | Transfer in progress |
| 1 | Paused | Transfer paused |
| 2 | Complete | Transfer completed |
| 3 | Failed | Transfer failed |

### 4.18 Error Frame (0x30)

Stream error notification.

**Payload:**
| Offset | Size | Field | Type | Description |
|--------|------|-------|------|-------------|
| 0 | 4 | ErrorCode | int | Error code |
| 4 | 8 | Position | long | Stream position at error |
| 12 | 2+N | Message | string | Error message |

### 4.19 Ack Frame (0x40)

Sliding window acknowledgment.

**Payload:**
| Offset | Size | Field | Type | Description |
|--------|------|-------|------|-------------|
| 0 | 4 | AckedSequence | uint | Highest contiguous sequence acknowledged |
| 4 | 4 | WindowSize | uint | Receiver's available window |

---

## 5. Stream Lifecycle

### 5.1 State Machine

Streams progress through the following states:

**States:**
| State | Description |
|-------|-------------|
| None | No stream active |
| Opening | Open request sent, awaiting response |
| Open | Stream is active and ready for operations |
| Closed | Stream has been closed |

**Transitions:**
- None → Opening: Open frame sent
- Opening → Open: OpenResponse received with Success=true
- Opening → Closed: OpenResponse received with Success=false
- Open → Closed: Close frame sent/received or error occurred
- Any → Closed: Protocol error or session disconnect

### 5.2 Client-Initiated Flow

1. Client calls `transport.OpenAsync(resourceId, access, share)`
2. Client sends Open frame
3. Server receives Open, calls handler via `ReceiveRequest()`
4. Server handler calls `ProvideFile()` or `ProvideStream()`
5. Server sends OpenResponse frame with metadata
6. Client receives OpenResponse, `OpenAsync()` returns `INexusStream`
7. Client performs read/write/seek operations
8. Client calls `stream.DisposeAsync()` or `Close()`
9. Close frame sent, stream enters Closed state

### 5.3 Server-Initiated Flow

1. Server calls `transport.OpenAsync(resourceId, access, share)`
2. Server sends Open frame
3. Client receives Open, calls handler via `ReceiveRequest()`
4. Client handler calls `ProvideFile()` or `ProvideStream()`
5. Client sends OpenResponse frame with metadata
6. Server receives OpenResponse, `OpenAsync()` returns `INexusStream`
7. Server performs read/write/seek operations
8. Server calls `stream.DisposeAsync()` or `Close()`
9. Close frame sent, stream enters Closed state

### 5.4 Multiple Streams Per Transport

A single `INexusStreamTransport` can handle multiple sequential streams:

1. First stream opened, used, and closed
2. Second stream opened on same transport
3. Process repeats until transport is disposed

The provider iterates over `ReceiveRequest()` to handle each request:

```csharp
await foreach (var request in transport.ReceiveRequest())
{
    await transport.ProvideFile(request.ResourceId);
    // ProvideFile blocks until stream is closed by initiator
}
```

### 5.5 ReadyTask Synchronization

The `INexusStreamTransport.ReadyTask` property:
- Completes when the underlying pipe handshake completes
- Resets when a new stream is provided by the server
- Must be awaited before calling `OpenAsync()`

---

## 6. Position and Synchronization

### 6.1 Position Authority

The server (provider) is authoritative for stream position. All position-changing operations require acknowledgment:

- **Seek:** Client sends Seek, waits for SeekResponse with new position
- **Read:** Position advances by bytes returned in Data frames
- **Write:** Client sends Write+Data, waits for WriteResponse with new position

### 6.2 Sequence Numbers

Data frames include a sequence number for ordering validation:
- Sequence starts at 0 for each read/write operation
- Increments by 1 for each Data frame
- DataEnd frame contains the final sequence number
- Receiver validates sequence continuity

### 6.3 Position Divergence Prevention

Position divergence is prevented by:
1. All position-changing operations wait for acknowledgment
2. Data frames use sequence numbers, not positions
3. WriteResponse includes the authoritative new position
4. SeekResponse includes the authoritative new position

If a sequence gap is detected:
1. Receiver sends Error frame with appropriate code
2. Stream transitions to Closed state
3. `Error` property on `INexusStream` is set

### 6.4 Non-Seekable Streams

For streams where `CanSeek = false`:
- Position is undefined
- Seek operations throw `NotSupportedException`
- Data flows in one direction without position tracking
- Sequence numbers still validate frame ordering

---

## 7. Metadata

### 7.1 Metadata Structure

**Wire format:**
| Offset | Size | Field | Type | Description |
|--------|------|-------|------|-------------|
| 0 | 8 | Length | long | Stream length (-1 if unknown) |
| 8 | 1 | Flags | byte | Metadata flags |
| 9 | 8 | Created | long | Creation time ticks (0 if unknown) |
| 17 | 8 | Modified | long | Modification time ticks (0 if unknown) |
| 25 | 2+N | ContentType | string? | MIME type (null if unknown) |

**Flags:**
| Bit | Name | Description |
|-----|------|-------------|
| 0 | HasKnownLength | Length field is valid |
| 1 | CanSeek | Stream supports seeking |
| 2 | CanRead | Stream supports reading |
| 3 | CanWrite | Stream supports writing |
| 4 | HasCreated | Created timestamp is valid |
| 5 | HasModified | Modified timestamp is valid |
| 6-7 | Reserved | Must be zero |

### 7.2 Required Fields

All streams MUST provide:
- `Length` (with `HasKnownLength` flag)
- `CanSeek`
- `CanRead`
- `CanWrite`

### 7.3 Optional Fields

File-backed streams SHOULD provide when available:
- `Created` (with `HasCreated` flag)
- `Modified` (with `HasModified` flag)
- `ContentType`

### 7.4 Accessing Extended Attributes

Platform-specific attributes (Windows file attributes, Unix permissions, last accessed time) are not included in the core metadata structure. Applications requiring these should:
1. Define custom methods on their Nexus interface
2. Use the resource ID to identify the file
3. Query attributes through application-level calls

---

## 8. Progress Notifications

### 8.1 Progress Tracking

Progress notifications are integral to the stream system and MUST be implemented.

### 8.2 Notification Triggers

Progress frames are sent when:
1. **Byte threshold:** Configurable bytes transferred (default: 1MB)
2. **Time interval:** Configurable interval elapsed (default: 5 seconds)
3. **Explicit request:** GetProgress frame received (reserved for future use)
4. **State change:** Transfer state changes (pause, resume, complete, fail)

### 8.3 Progress Data

| Field | Description |
|-------|-------------|
| BytesTransferred | Total bytes transferred in current operation |
| TotalBytes | Expected total bytes (-1 if unknown) |
| ElapsedTicks | TimeSpan since stream opened |
| BytesPerSecond | Current transfer rate |
| State | Active, Paused, Complete, or Failed |

### 8.4 Unknown Length Streams

For streams without known length:
- `TotalBytes` = -1
- `BytesTransferred` shows actual bytes transferred
- `ElapsedTicks` shows duration
- Progress percentage cannot be calculated (UI should show indeterminate)

### 8.5 Progress Overwriting

New progress notifications overwrite previous state. The receiver maintains only the most recent progress information.

---

## 9. Error Handling

### 9.1 Error Categories

**Stream Errors (recoverable, do not disconnect session):**
| Code | Name | Description |
|------|------|-------------|
| 1 | FileNotFound | Resource does not exist |
| 2 | AccessDenied | Permission denied |
| 3 | SharingViolation | Resource locked by another |
| 4 | DiskFull | Storage capacity exceeded |
| 5 | IoError | General I/O failure |
| 6 | InvalidOperation | Operation not valid for current state |
| 7 | Timeout | Operation timed out |
| 8 | Cancelled | Operation was cancelled |
| 9 | EndOfStream | Unexpected end of stream |
| 10 | SeekError | Seek operation failed |

**Protocol Errors (disconnect session):**
| Code | Name | Description |
|------|------|-------------|
| 100 | InvalidFrameType | Unknown frame type received |
| 101 | InvalidFrameSequence | Frames received in wrong order |
| 102 | MalformedFrame | Frame structure is invalid |
| 103 | SequenceGap | Data frame sequence number gap |
| 104 | UnexpectedFrame | Frame not valid for current state |

### 9.2 Error Handling Behavior

**Stream errors:**
1. Error frame sent with error code and position
2. Stream transitions to Closed state
3. `INexusStream.Error` property set to exception
4. Session remains connected
5. New stream can be opened on same transport

**Protocol errors:**
1. Session disconnects with `ProtocolError` reason
2. All active streams on session are closed
3. Reconnection required

### 9.3 Stream State on Error

```csharp
public interface INexusStream : IAsyncDisposable
{
    NexusStreamState State { get; }  // Opening, Open, Closed
    Exception? Error { get; }        // null = graceful, non-null = failure
    // ...
}
```

- `State == Closed` with `Error == null`: Graceful close
- `State == Closed` with `Error != null`: Closed due to error

### 9.4 Partial Write Failures

When a write operation fails partway:
1. Error frame sent with `Position` = last successful position
2. WriteResponse sent with `BytesWritten` = actual bytes written
3. Stream may remain open for retry (depending on error type)
4. Client can resume from reported position

---

## 10. Concurrency Model

### 10.1 Thread Safety

The `INexusStreamTransport` and `INexusStream` interfaces are designed for concurrent access with the following guarantees:

- Multiple threads may call methods concurrently
- Internal async locks serialize operations as needed
- The underlying pipe is protected from concurrent access

### 10.2 Locking Strategy

**Separate read/write locks:**
- Read operations acquire read lock (`SemaphoreSlim(1,1)`)
- Write operations acquire write lock (`SemaphoreSlim(1,1)`)
- Read and write may proceed concurrently (full-duplex)
- Seek acquires both locks (exclusive operation)

### 10.3 Single Position Model

Streams maintain a single position (like `System.IO.FileStream`):
- Read advances position by bytes read
- Write advances position by bytes written
- Seek changes position explicitly

For concurrent read/write with seeking, the caller is responsible for coordination.

### 10.4 Non-Seekable Stream Concurrency

For non-seekable streams:
- Read and write operate independently
- No position coordination required
- Full-duplex operation is natural

### 10.5 Frame Interleaving

When read and write operations are concurrent:
- Outgoing pipe: Write frames and control frames interleave
- Incoming pipe: Data frames and response frames interleave
- Frame reader routes frames by type to appropriate handlers

---

## 11. Public API

### 11.1 INexusStreamTransport

```csharp
/// <summary>
/// Transport for stream operations over a NexNet pipe.
/// Passed to proxy methods as an argument.
/// </summary>
public interface INexusStreamTransport : IAsyncDisposable
{
    /// <summary>
    /// Task that completes when the underlying pipe is ready.
    /// Resets when a new stream is provided.
    /// </summary>
    Task ReadyTask { get; }

    /// <summary>
    /// Opens a stream for the given resource identifier.
    /// </summary>
    /// <param name="resourceId">Server-interpreted resource identifier (max 2000 chars).</param>
    /// <param name="access">Requested access mode.</param>
    /// <param name="share">Sharing mode.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The opened stream.</returns>
    ValueTask<INexusStream> OpenAsync(
        string resourceId,
        StreamAccessMode access,
        StreamShareMode share = StreamShareMode.None,
        CancellationToken ct = default);

    /// <summary>
    /// Receives stream requests from the remote endpoint.
    /// Server-side only.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Async enumerable of stream requests.</returns>
    IAsyncEnumerable<INexusStreamRequest> ReceiveRequest(
        CancellationToken ct = default);

    /// <summary>
    /// Provides a file stream in response to a request.
    /// Server-side only. Blocks until the stream is closed.
    /// </summary>
    /// <param name="path">File path to provide.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask ProvideFile(string path, CancellationToken ct = default);

    /// <summary>
    /// Provides an arbitrary stream in response to a request.
    /// Server-side only. Blocks until the stream is closed.
    /// </summary>
    /// <param name="stream">Stream to provide.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask ProvideStream(Stream stream, CancellationToken ct = default);
}
```

### 11.2 INexusStream

```csharp
/// <summary>
/// An active stream for reading and writing data.
/// </summary>
public interface INexusStream : IAsyncDisposable
{
    /// <summary>
    /// Current state of the stream.
    /// </summary>
    NexusStreamState State { get; }

    /// <summary>
    /// Error that caused the stream to close, or null for graceful close.
    /// </summary>
    Exception? Error { get; }

    /// <summary>
    /// Current position in the stream.
    /// Undefined for non-seekable streams.
    /// </summary>
    long Position { get; }

    /// <summary>
    /// Length of the stream, or -1 if unknown.
    /// </summary>
    long Length { get; }

    /// <summary>
    /// True if the stream length is known.
    /// </summary>
    bool HasKnownLength { get; }

    /// <summary>
    /// True if the stream supports seeking.
    /// </summary>
    bool CanSeek { get; }

    /// <summary>
    /// True if the stream supports reading.
    /// </summary>
    bool CanRead { get; }

    /// <summary>
    /// True if the stream supports writing.
    /// </summary>
    bool CanWrite { get; }

    /// <summary>
    /// Reads data from the stream.
    /// </summary>
    /// <param name="buffer">Buffer to read into.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Number of bytes read.</returns>
    ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default);

    /// <summary>
    /// Writes data to the stream.
    /// </summary>
    /// <param name="data">Data to write.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default);

    /// <summary>
    /// Seeks to a position in the stream.
    /// Throws NotSupportedException if CanSeek is false.
    /// </summary>
    /// <param name="offset">Seek offset.</param>
    /// <param name="origin">Seek origin.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>New absolute position.</returns>
    ValueTask<long> SeekAsync(long offset, SeekOrigin origin, CancellationToken ct = default);

    /// <summary>
    /// Flushes buffered data to storage.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    ValueTask FlushAsync(CancellationToken ct = default);

    /// <summary>
    /// Gets the current stream metadata.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Current metadata.</returns>
    ValueTask<NexusStreamMetadata> GetMetadataAsync(CancellationToken ct = default);

    /// <summary>
    /// Gets a System.IO.Stream wrapper for this stream.
    /// The wrapper adds overhead; prefer direct methods when possible.
    /// </summary>
    /// <returns>Stream wrapper.</returns>
    Stream GetStream();

    /// <summary>
    /// Observable for progress updates.
    /// </summary>
    IObservable<NexusStreamProgress> Progress { get; }
}
```

### 11.3 INexusStreamRequest

```csharp
/// <summary>
/// A request to open a stream from the remote endpoint.
/// </summary>
public interface INexusStreamRequest
{
    /// <summary>
    /// The requested resource identifier.
    /// </summary>
    string ResourceId { get; }

    /// <summary>
    /// The requested access mode.
    /// </summary>
    StreamAccessMode Access { get; }

    /// <summary>
    /// The requested sharing mode.
    /// </summary>
    StreamShareMode Share { get; }

    /// <summary>
    /// Resume position if resuming, or -1 for fresh start.
    /// </summary>
    long ResumePosition { get; }
}
```

### 11.4 Supporting Types

```csharp
public enum NexusStreamState
{
    Opening,
    Open,
    Closed
}

public enum StreamAccessMode : byte
{
    Read = 0x01,
    Write = 0x02,
    ReadWrite = Read | Write
}

public enum StreamShareMode : byte
{
    None = 0x00,
    Read = 0x01,
    Write = 0x02,
    ReadWrite = Read | Write
}

public readonly struct NexusStreamMetadata
{
    public long Length { get; init; }
    public bool HasKnownLength { get; init; }
    public bool CanSeek { get; init; }
    public bool CanRead { get; init; }
    public bool CanWrite { get; init; }
    public DateTimeOffset? Created { get; init; }
    public DateTimeOffset? Modified { get; init; }
    public string? ContentType { get; init; }
}

public readonly struct NexusStreamProgress
{
    public long BytesTransferred { get; init; }
    public long TotalBytes { get; init; }
    public TimeSpan Elapsed { get; init; }
    public double BytesPerSecond { get; init; }
    public TransferState State { get; init; }
}

public enum TransferState : byte
{
    Active = 0,
    Paused = 1,
    Complete = 2,
    Failed = 3
}
```

### 11.5 Acquisition Methods

**Client-side:**
```csharp
// On INexusClient
IRentedNexusStreamTransport CreateStream();
```

**Server-side:**
```csharp
// On SessionContext<TProxy>
IRentedNexusStreamTransport CreateStream();
```

### 11.6 Usage Example

**Client:**
```csharp
var transport = client.CreateStream();
await client.Proxy.FileOperation(transport);
await transport.ReadyTask;

await using var stream = await transport.OpenAsync(
    "/files/document.pdf",
    StreamAccessMode.Read);

var buffer = new byte[8192];
int bytesRead;
while ((bytesRead = await stream.ReadAsync(buffer)) > 0)
{
    // Process data
}
```

**Server:**
```csharp
[NexusMethod]
public async ValueTask FileOperation(INexusStreamTransport transport)
{
    await foreach (var request in transport.ReceiveRequest())
    {
        // Validate request.ResourceId
        await transport.ProvideFile(request.ResourceId);
        // ProvideFile blocks until client closes the stream
    }
}
```

---

## 12. Implementation Requirements

### 12.1 File Location

All NexStream implementation code MUST reside in:
```
src/NexNet/Pipes/Stream/
```

### 12.2 Frame Reader/Writer

**NexusStreamFrameWriter:**
- Uses existing `INexusDuplexPipe.Output` for writing
- Serializes frames to raw binary format
- Chunks large payloads based on configurable size
- Thread-safe with async locking

**NexusStreamFrameReader:**
- Uses existing `INexusDuplexPipe.Input` for reading
- Parses raw binary frames
- Validates frame structure and sequence
- Routes frames to handlers by type
- Thread-safe with async locking

### 12.3 Integration with Existing Infrastructure

- Use existing pipe back-pressure (water marks) automatically
- Use existing pipe lifecycle (ReadyTask, CompleteTask)
- Frame payload size is independent of protocol message size
- One stream per pipe, up to 255 concurrent streams per session

### 12.4 Testing Requirements

Full test suite required for:
- Frame serialization/deserialization (all frame types)
- State machine transitions
- Position synchronization
- Error handling paths
- Concurrent read/write operations
- Progress notification timing
- Resume functionality
- Large file handling (streaming without full buffering)

---

## 13. Security Considerations

### 13.1 Resource ID Validation

- Maximum length: 2000 characters
- Server is responsible for all validation
- Protocol does not interpret resource ID semantics
- Applications MUST sanitize resource IDs to prevent path traversal

### 13.2 Access Control

- Server MUST validate access permissions before providing streams
- ShareMode is advisory; server enforces actual sharing policy
- File locking uses OS-level mechanisms when available

### 13.3 Denial of Service

- Frame payload size limits prevent memory exhaustion
- Existing pipe water marks provide back-pressure
- Progress notifications have minimum intervals to prevent flooding

### 13.4 Error Information Disclosure

- Error messages SHOULD be sanitized before sending
- Internal paths and system information SHOULD NOT be included in error frames
- Error codes provide enough detail for client handling without exposing internals

---

## 14. Future Extensions

### 14.1 Phase 2: Locking

| Frame Type | Description |
|------------|-------------|
| Lock | Acquire byte-range lock |
| LockResponse | Lock acquisition result |
| Unlock | Release lock |
| UnlockResponse | Unlock result |

Lock semantics:
- Exclusive and shared modes
- Lease-based with configurable timeout
- Auto-release on error or disconnect
- OS-level locking for file streams

### 14.2 Phase 2: Metadata Watching

| Frame Type | Description |
|------------|-------------|
| Watch | Subscribe to metadata changes |
| WatchResponse | Subscription confirmation |
| MetadataChanged | Pushed metadata update |
| Unwatch | Unsubscribe from changes |

### 14.3 Reserved Frame Types

| Range | Purpose |
|-------|---------|
| 0x50-0x5F | Locking operations |
| 0x60-0x6F | Watch/notification operations |
| 0x70-0x7F | Compression negotiation |
| 0x80-0xFF | Reserved for future protocol versions |

---

## Appendix A: Wire Format Examples

### A.1 Open Frame

```
01 1E 00 00 00 0F 00 2F 66 69 6C 65 73 2F 74 65
73 74 2E 74 78 74 01 00 FF FF FF FF FF FF FF FF

Type: 0x01 (Open)
Length: 0x0000001E (30 bytes)
ResourceId: "/files/test.txt" (15 chars)
Access: 0x01 (Read)
Share: 0x00 (None)
ResumePosition: -1 (fresh start)
```

### A.2 Data Frame

```
10 08 00 00 00 00 00 00 00 48 65 6C 6C 6F 21 0A

Type: 0x10 (Data)
Length: 0x00000008 (8 bytes)
Sequence: 0x00000000 (0)
Data: "Hello!\n" (7 bytes)
```

### A.3 Error Frame

```
30 1A 00 00 00 02 00 00 00 00 10 00 00 00 00 00
00 00 0D 00 41 63 63 65 73 73 20 64 65 6E 69 65 64

Type: 0x30 (Error)
Length: 0x0000001A (26 bytes)
ErrorCode: 0x00000002 (AccessDenied)
Position: 0x0000000000001000 (4096)
Message: "Access denied" (13 chars)
```

---

## Appendix B: Error Code Reference

| Code | Name | Description | Recoverable |
|------|------|-------------|-------------|
| 0 | Success | No error | N/A |
| 1 | FileNotFound | Resource does not exist | Yes |
| 2 | AccessDenied | Permission denied | Yes |
| 3 | SharingViolation | Resource locked | Yes |
| 4 | DiskFull | Storage capacity exceeded | Yes |
| 5 | IoError | General I/O failure | Yes |
| 6 | InvalidOperation | Operation not valid | Yes |
| 7 | Timeout | Operation timed out | Yes |
| 8 | Cancelled | Operation cancelled | Yes |
| 9 | EndOfStream | Unexpected end | Yes |
| 10 | SeekError | Seek failed | Yes |
| 100 | InvalidFrameType | Unknown frame type | No |
| 101 | InvalidFrameSequence | Wrong frame order | No |
| 102 | MalformedFrame | Invalid frame structure | No |
| 103 | SequenceGap | Data sequence gap | No |
| 104 | UnexpectedFrame | Frame not valid for state | No |
