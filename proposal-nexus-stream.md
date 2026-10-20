# NexStream Transport Proposal

## Overview

A sub-protocol built on `INexusDuplexPipe` that provides file-like stream semantics with bidirectional message/data framing, supporting remote file operations similar to SMB/Samba.

---

## Core Architecture

```
┌─────────────────────────────────────────────────────────────┐
│                    NexStream Protocol                       │
├─────────────────────────────────────────────────────────────┤
│  ┌──────────────┐    ┌──────────────┐    ┌───────────────┐  │
│  │ Control Msgs │    │  Binary Data │    │  Notifications│  │
│  │ (Commands)   │    │  (Chunks)    │    │  (Progress)   │  │
│  └──────────────┘    └──────────────┘    └───────────────┘  │
├─────────────────────────────────────────────────────────────┤
│                  Message Framing Layer                      │
│         [Type:1][Flags:1][Length:4][Payload:N]              │
├─────────────────────────────────────────────────────────────┤
│                   INexusDuplexPipe                          │
│         (Back-pressure, State Machine, Chunking)            │
├─────────────────────────────────────────────────────────────┤
│                      Transport                              │
│              (TCP, UDS, HttpSocket, etc.)                   │
└─────────────────────────────────────────────────────────────┘
```

---

## Message Framing Format

Each frame on the pipe would follow this structure:

```
┌─────────┬─────────┬──────────┬────────────────────┐
│ Type    │ Flags   │ Length   │ Payload            │
│ (1 byte)│ (1 byte)│ (4 bytes)│ (variable)         │
└─────────┴─────────┴──────────┴────────────────────┘
```

### Frame Types

| Type | Name | Description |
|------|------|-------------|
| 0x01 | `Open` | Open stream with mode/access |
| 0x02 | `Close` | Close stream handle |
| 0x03 | `Read` | Request data read |
| 0x04 | `Write` | Write data to stream |
| 0x05 | `Seek` | Change position |
| 0x06 | `Flush` | Flush buffers |
| 0x10 | `Data` | Binary data chunk |
| 0x11 | `DataEnd` | Final chunk marker |
| 0x20 | `Lock` | Acquire lock on range |
| 0x21 | `Unlock` | Release lock |
| 0x30 | `Metadata` | Stream metadata (size, modified, etc.) |
| 0x40 | `Progress` | Transfer progress notification |
| 0x50 | `Error` | Error response |
| 0x51 | `Ack` | Acknowledgment |

### Flags

| Bit | Meaning |
|-----|---------|
| 0x01 | Request (vs Response) |
| 0x02 | Requires ACK |
| 0x04 | Last fragment |
| 0x08 | Compressed |
| 0x10 | Encrypted |

---

## Core Interfaces

```csharp
/// <summary>
/// A seekable, lockable stream over INexusDuplexPipe
/// </summary>
public interface INexusStream : IAsyncDisposable
{
    /// <summary>Stream identifier for this session</summary>
    ushort StreamId { get; }

    /// <summary>Current position in the stream</summary>
    long Position { get; }

    /// <summary>Length of the underlying resource (if known)</summary>
    long? Length { get; }

    /// <summary>Whether the stream supports seeking</summary>
    bool CanSeek { get; }

    /// <summary>Whether the stream supports reading</summary>
    bool CanRead { get; }

    /// <summary>Whether the stream supports writing</summary>
    bool CanWrite { get; }

    // Core operations
    ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default);
    ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default);
    ValueTask<long> SeekAsync(long offset, SeekOrigin origin, CancellationToken ct = default);
    ValueTask FlushAsync(CancellationToken ct = default);

    // Locking
    ValueTask<IAsyncDisposable> LockRangeAsync(long offset, long length,
        StreamLockMode mode, CancellationToken ct = default);

    // Metadata
    ValueTask<StreamMetadata> GetMetadataAsync(CancellationToken ct = default);

    // Progress notifications
    IAsyncEnumerable<TransferProgress> ProgressUpdates { get; }
}

public interface INexusStreamTransport
{
    /// <summary>
    /// Opens a stream for the given resource identifier
    /// </summary>
    ValueTask<INexusStream> OpenAsync(
        string resourceId,
        StreamAccessMode access,
        StreamShareMode share,
        CancellationToken ct = default);
}
```

---

## Key Challenges & Solutions

### 1. Bidirectional Command/Data Multiplexing

**Challenge:** Need to send control messages (seek, lock, metadata requests) while potentially streaming binary data in either direction.

**Solution:**
- Use the frame type byte to distinguish message types
- Control messages are small, serialized with MemoryPack
- Binary data uses `Data` frames with sequence numbers
- The existing `NexusPipeWriter` chunks at 8KB by default—leverage this

```csharp
// Frame parsing on receive
var frameType = (NexStreamFrameType)buffer[0];
var flags = (NexStreamFlags)buffer[1];
var length = BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(2));

if (frameType == NexStreamFrameType.Data)
{
    // Route to data buffer
    _dataBuffer.BufferData(buffer.Slice(6, length));
}
else
{
    // Deserialize and dispatch control message
    await DispatchControlMessageAsync(frameType, buffer.Slice(6, length));
}
```

### 2. Position Synchronization & Seek

**Challenge:** Both sides need to agree on the current position, especially after seeks or partial writes.

**Solution:**
- Server is authoritative for position
- All position-changing operations return the new position
- Include position in `Data` frame headers for verification

```
Data frame extended header:
┌──────────┬───────────┬──────────┬──────────┬───────────────┐
│ Type=0x10│ Flags     │ Length   │ Position │ Payload       │
│ (1)      │ (1)       │ (4)      │ (8)      │ (N)           │
└──────────┴───────────┴──────────┴──────────┴───────────────┘
```

### 3. Resume After Disconnect

**Challenge:** Long transfers may be interrupted; need to resume from last position.

**Solution:**
- Each invocation/pipe is one "session"
- Client tracks last confirmed position
- On reconnect, open with `OpenAsync()` passing a resume position
- Server validates resume position against its state

```csharp
public record StreamOpenRequest
{
    public string ResourceId { get; init; }
    public StreamAccessMode Access { get; init; }
    public long? ResumePosition { get; init; }  // null = start fresh
    public byte[]? ResumeToken { get; init; }   // server-issued resume token
}
```

### 4. Back-Pressure Integration

**Challenge:** Must respect NexNet's water mark system to prevent memory exhaustion.

**Solution:**
- Leverage existing `NexusPipeWriter.PauseWriting` mechanism
- When high water mark hit, pause writing automatically
- Progress notifications include buffer state

```csharp
// The NexusPipeWriter already handles this:
// - HighWaterMark (192KB) triggers notification
// - HighWaterCutoff (256KB) causes exponential backoff
// NexStream just needs to respect the PauseWriting property
```

### 5. File Locking Semantics

**Challenge:** Need shared/exclusive byte-range locks across multiple clients.

**Solution:**
- Lock manager on server side tracks ranges per resource
- Locks have lease timeouts (configurable, e.g., 30s)
- Client must periodically renew or lock expires
- Lock state NOT persisted across session—intentional for simplicity

```csharp
public enum StreamLockMode
{
    Shared,      // Multiple readers allowed
    Exclusive    // Single writer, no readers
}

public record LockRequest
{
    public long Offset { get; init; }
    public long Length { get; init; }  // -1 = entire file
    public StreamLockMode Mode { get; init; }
    public TimeSpan LeaseTime { get; init; }
}
```

### 6. Large File Handling

**Challenge:** Files may exceed memory; can't buffer entire content.

**Solution:**
- Streaming by design—chunks flow through pipe
- Use `ReadBatchUntilComplete()` pattern
- Configurable chunk size (8KB default, configurable up to 1MB)
- No full-file buffering ever

### 7. Error Recovery & Partial Failures

**Challenge:** What happens if write fails mid-stream?

**Solution:**
- Error frames include last successful position
- Client can decide to retry or abort
- ACK flag enables confirmed writes

```csharp
public record ErrorFrame
{
    public NexStreamErrorCode Code { get; init; }
    public string? Message { get; init; }
    public long? LastConfirmedPosition { get; init; }
}
```

### 8. One Transfer Per Invocation

**Challenge:** Single pipe per file operation—how to handle multiple concurrent files?

**Solution:**
- Each `OpenAsync()` allocates a new duplex pipe via `RentPipe()`
- Multiple files = multiple pipes (up to 255 concurrent per session)
- Pipe released on `Close` or `DisposeAsync`

```csharp
// On server side:
[NexusMethod]
public async ValueTask<INexusDuplexPipe> OpenFileStream(StreamOpenRequest request)
{
    var pipe = Context.PipeManager.RentPipe();

    // Spawn background task to handle this file's I/O
    _ = HandleFileStreamAsync(pipe, request);

    return pipe;
}
```

### 9. Progress Notification System

**Challenge:** Need real-time transfer progress without blocking data flow.

**Solution:**
- Progress sent as low-priority control messages
- Configurable interval (e.g., every 1MB or 5 seconds)
- Includes bytes transferred, rate, ETA

```csharp
public record TransferProgress
{
    public long BytesTransferred { get; init; }
    public long TotalBytes { get; init; }  // -1 if unknown
    public double BytesPerSecond { get; init; }
    public TimeSpan Elapsed { get; init; }
    public TimeSpan? EstimatedRemaining { get; init; }
    public TransferState State { get; init; }  // Active, Paused, Complete, Failed
}

// Exposed as IAsyncEnumerable for easy consumption
await foreach (var progress in stream.ProgressUpdates)
{
    Console.WriteLine($"{progress.BytesTransferred}/{progress.TotalBytes}");
}
```

### 10. Metadata Synchronization

**Challenge:** File attributes (size, timestamps, permissions) may change during access.

**Solution:**
- Metadata fetched on-demand, not cached long-term
- `MetadataChanged` notifications pushed if watching enabled
- Metadata frame includes version/etag for conflict detection

```csharp
public record StreamMetadata
{
    public long Size { get; init; }
    public DateTimeOffset Created { get; init; }
    public DateTimeOffset Modified { get; init; }
    public DateTimeOffset Accessed { get; init; }
    public StreamAttributes Attributes { get; init; }
    public string? ContentType { get; init; }
    public string? ETag { get; init; }
}
```

---

## State Machine

```
┌──────────┐   OpenAsync    ┌──────────┐   Read/Write   ┌──────────┐
│  Closed  │ ─────────────> │  Ready   │ ─────────────> │ Active   │
└──────────┘                └──────────┘                └──────────┘
     ↑                           │                           │
     │                           │ Error                     │
     │                           ↓                           │
     │                      ┌──────────┐                     │
     │                      │  Faulted │                     │
     │                      └──────────┘                     │
     │                                                       │
     │   Close/Dispose                                       │
     └───────────────────────────────────────────────────────┘
```

---

## Additional Challenges to Consider

| Challenge | Complexity | Notes |
|-----------|------------|-------|
| **Checksum verification** | Medium | Optional frame flag to include CRC32/xxHash |
| **Compression** | Medium | Per-frame or stream-level; flag in header |
| **Encryption** | High | If not using TLS, per-frame encryption needed |
| **Sparse file support** | Medium | Seek beyond EOF, hole detection |
| **Append-only mode** | Low | Simplified write mode, position always at end |
| **Atomic operations** | High | Multi-step ops (rename+write) as transactions |
| **Quota enforcement** | Medium | Server-side; reject writes exceeding quota |
| **Timeout handling** | Medium | Idle timeout per stream vs per session |

---

## Recommended Implementation Phases

### Phase 1: Core Transport
- Frame parser/writer on INexusDuplexPipe
- Open/Close/Read/Write/Seek commands
- Data streaming with position tracking
- Basic progress notifications

### Phase 2: Reliability
- Resume token generation and validation
- Error frames with position recovery
- ACK-based confirmed writes

### Phase 3: Locking
- Byte-range locking
- Lock manager with lease expiration
- Deadlock detection (optional)

### Phase 4: Advanced Features
- Metadata watching
- Compression support
- Sparse file hints

---

## Summary

Building a file streaming transport on `INexusDuplexPipe` is viable because NexNet already provides:

- Back-pressure via water marks
- Automatic chunking (8KB)
- State synchronization
- Pipe lifecycle management
- Multiple concurrent pipes (up to 255)

**Key challenges** are:

1. **Protocol design** — Mixing control messages with binary data on single pipe
2. **Position coherence** — Keeping client/server synchronized after seeks
3. **Resume semantics** — Server-issued tokens for resumable transfers
4. **Locking** — Cross-client byte-range locks with lease management
5. **Error recovery** — Partial failure handling with position checkpoints

The one-transfer-per-invocation model maps cleanly to NexNet's pipe rental system, and the existing flow control mechanisms handle the back-pressure automatically.
