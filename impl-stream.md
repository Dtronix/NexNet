# NexStream Implementation Plan

**Version:** 1.0
**Status:** Planning
**Reference:** [spec-stream.md](spec-stream.md)

## Overview

This document outlines a 6-phase implementation plan for NexStream, a sub-protocol providing stream semantics over `INexusDuplexPipe`. Each phase builds incrementally with dedicated tests.

### Design Decisions

Based on requirements analysis:

| Decision | Choice | Rationale |
|----------|--------|-----------|
| Flow Control | Existing pipe back-pressure | Leverage NexusPipeManager water marks initially |
| Compression | Deferred | Not in initial 6 phases; add as Phase 7 |
| Resumption | Phase 5 | Essential feature, requires metadata foundation |
| Ack Frames | Phase 6 | Optional optimization after core is stable |
| Cancellation | Drain pending frames | Keep pipe in sync state before stopping |
| Partial Reads | Return available | Match System.IO.Stream semantics |
| Payload Size Config | ClientConfig/ServerConfig | Instance property on existing config classes |
| Progress Threading | Fire on any thread | No sync context marshaling overhead |
| Path Security | Application layer | Transport does not validate paths |
| SetLength | Supported | Allow truncate/extend operations |
| Concurrent Open | Throw exception | One stream at a time per transport |
| Operation Timeouts | CancellationToken only | Caller provides timeout via CancellationTokenSource |
| Sync Wrapper Methods | Sync-over-async | Document limitation, prefer async methods |
| Error Recovery | Transport reusable | New stream can open after non-protocol error |
| Progress Counters | Separate read/write | Track BytesRead and BytesWritten independently |
| API Evolution | Include from start | resumePosition=-1 default in Phase 2 signature |
| Session Disconnect | Throw immediately | Pending operations throw with disconnect reason |
| Frame Types | All structs | Use Memory<byte> for variable data, accept copying |
| Extra Data Received | Protocol error | More data than requested = protocol violation |
| Access Validation | Client-side throw | Check CanRead/CanWrite locally before sending |

### File Structure

All implementation in `src/NexNet/Pipes/NexusStream/`:

```
src/NexNet/Pipes/NexusStream/
├── Frames/
│   ├── FrameType.cs
│   ├── FrameHeader.cs
│   ├── OpenFrame.cs
│   ├── OpenResponseFrame.cs
│   ├── CloseFrame.cs
│   ├── SeekFrame.cs
│   ├── SeekResponseFrame.cs
│   ├── FlushFrame.cs
│   ├── FlushResponseFrame.cs
│   ├── SetLengthFrame.cs
│   ├── SetLengthResponseFrame.cs
│   ├── GetMetadataFrame.cs
│   ├── MetadataResponseFrame.cs
│   ├── ReadFrame.cs
│   ├── WriteFrame.cs
│   ├── WriteResponseFrame.cs
│   ├── DataFrame.cs
│   ├── DataEndFrame.cs
│   ├── ProgressFrame.cs
│   ├── ErrorFrame.cs
│   └── AckFrame.cs
├── NexusStreamFrameWriter.cs
├── NexusStreamFrameReader.cs
├── NexusStreamTransport.cs
├── NexusStream.cs
├── NexusStreamRequest.cs
├── NexusStreamMetadata.cs
├── NexusStreamProgress.cs
├── NexusStreamWrapper.cs
├── Enums/
│   ├── NexusStreamState.cs
│   ├── StreamAccessMode.cs
│   ├── StreamShareMode.cs
│   ├── TransferState.cs
│   └── StreamErrorCode.cs
└── Interfaces/
    ├── INexusStreamTransport.cs
    ├── INexusStream.cs
    └── INexusStreamRequest.cs
```

Test structure mirrors implementation:

```
src/NexNet.IntegrationTests/NexusStream/
├── Frames/
│   └── *FrameTests.cs (one per frame type)
├── NexusStreamFrameWriterTests.cs
├── NexusStreamFrameReaderTests.cs
├── NexusStreamTransportTests.cs
├── NexusStreamTests.cs
├── StateMachineTests.cs
├── PositionSynchronizationTests.cs
├── ConcurrencyTests.cs
├── ProgressTests.cs
├── ResumptionTests.cs
└── Integration/
    ├── FileTransferTests.cs
    ├── GenericStreamTests.cs
    └── EndToEndTests.cs
```

---

## Phase 1: Frame Foundation

**Goal:** Establish frame serialization infrastructure and basic frame types.

### Components

#### 1.1 Frame Header & Type Registry

```csharp
// Enums/FrameType.cs
public enum FrameType : byte
{
    Open = 0x01,
    OpenResponse = 0x02,
    Close = 0x03,
    Seek = 0x04,
    SeekResponse = 0x05,
    Flush = 0x06,
    FlushResponse = 0x07,
    GetMetadata = 0x08,
    MetadataResponse = 0x09,
    Read = 0x0A,
    Write = 0x0B,
    WriteResponse = 0x0C,
    SetLength = 0x0D,
    SetLengthResponse = 0x0E,
    Data = 0x10,
    DataEnd = 0x11,
    Progress = 0x20,
    Error = 0x30,
    Ack = 0x40
}
```

```csharp
// Frames/FrameHeader.cs
public readonly struct FrameHeader
{
    public const int Size = 5;

    public FrameType Type { get; init; }
    public int PayloadLength { get; init; }

    public static FrameHeader Read(ReadOnlySpan<byte> buffer);
    public void Write(Span<byte> buffer);
}
```

#### 1.2 Binary Serialization Helpers

```csharp
// Internal helper for string encoding per spec §3.4
internal static class StreamBinaryHelpers
{
    // String: 2-byte length prefix (ushort LE) + UTF-8 bytes
    // Null string: 0xFFFF length
    public static int WriteString(Span<byte> buffer, string? value);
    public static string? ReadString(ReadOnlySpan<byte> buffer, out int bytesRead);

    // Little-endian integer helpers
    public static void WriteInt32(Span<byte> buffer, int value);
    public static void WriteInt64(Span<byte> buffer, long value);
    public static void WriteUInt32(Span<byte> buffer, uint value);
    // ... read equivalents
}
```

#### 1.3 Frame Writer Infrastructure

**Configuration:** Max payload size is configured via `ClientConfig.StreamMaxPayloadSize` / `ServerConfig.StreamMaxPayloadSize` (default: 65536 bytes).

```csharp
// NexusStreamFrameWriter.cs
internal sealed class NexusStreamFrameWriter
{
    private readonly PipeWriter _output;
    private readonly SemaphoreSlim _writeLock;
    private readonly int _maxPayloadSize;

    public NexusStreamFrameWriter(PipeWriter output, int maxPayloadSize);

    // Low-level frame writing
    public ValueTask WriteFrameAsync(FrameType type, ReadOnlyMemory<byte> payload, CancellationToken ct);

    // Typed frame writers (added incrementally per phase)
    public ValueTask WriteOpenAsync(OpenFrame frame, CancellationToken ct);
    public ValueTask WriteCloseAsync(CloseFrame frame, CancellationToken ct);
    public ValueTask WriteErrorAsync(ErrorFrame frame, CancellationToken ct);
}
```

#### 1.4 Frame Reader Infrastructure

```csharp
// NexusStreamFrameReader.cs
internal sealed class NexusStreamFrameReader
{
    private readonly PipeReader _input;

    public NexusStreamFrameReader(PipeReader input);

    // Reads next frame header + payload
    public ValueTask<(FrameHeader Header, ReadOnlyMemory<byte> Payload)?> ReadFrameAsync(CancellationToken ct);

    // Frame parsing (added incrementally per phase)
    public static OpenFrame ParseOpen(ReadOnlySpan<byte> payload);
    public static CloseFrame ParseClose(ReadOnlySpan<byte> payload);
    public static ErrorFrame ParseError(ReadOnlySpan<byte> payload);
}
```

#### 1.5 Basic Frame Structures

```csharp
// Frames/OpenFrame.cs
public readonly struct OpenFrame
{
    public string ResourceId { get; init; }
    public StreamAccessMode Access { get; init; }
    public StreamShareMode Share { get; init; }
    public long ResumePosition { get; init; }  // -1 = fresh start

    public int GetPayloadSize();
    public void Write(Span<byte> buffer);
    public static OpenFrame Read(ReadOnlySpan<byte> buffer);
}

// Frames/CloseFrame.cs
public readonly struct CloseFrame
{
    public bool Graceful { get; init; }
    // ... similar pattern
}

// Frames/ErrorFrame.cs
public readonly struct ErrorFrame
{
    public int ErrorCode { get; init; }
    public long Position { get; init; }
    public string Message { get; init; }
    // ...
}
```

### Phase 1 Tests

| Test Class | Coverage |
|------------|----------|
| `FrameHeaderTests` | Header read/write, endianness, boundary values |
| `StreamBinaryHelpersTests` | String encoding, null handling, max length (2000 chars) |
| `OpenFrameTests` | Serialization roundtrip, all access/share modes |
| `CloseFrameTests` | Graceful/non-graceful serialization |
| `ErrorFrameTests` | All error codes, message encoding, position values |
| `NexusStreamFrameWriterTests` | Frame output format, payload chunking |
| `NexusStreamFrameReaderTests` | Frame parsing, incomplete frame handling |

### Phase 1 Deliverables

- [ ] `FrameType` enum with all values
- [ ] `FrameHeader` struct with read/write
- [ ] `StreamBinaryHelpers` static class
- [ ] `OpenFrame`, `CloseFrame`, `ErrorFrame` structs
- [ ] `NexusStreamFrameWriter` (basic frame writing)
- [ ] `NexusStreamFrameReader` (basic frame reading)
- [ ] `StreamAccessMode`, `StreamShareMode` enums
- [ ] `StreamErrorCode` enum
- [ ] Unit tests for all above

---

## Phase 2: State Machine & Lifecycle

**Goal:** Implement stream state machine and open/close lifecycle.

### Components

#### 2.1 Stream State

```csharp
// Enums/NexusStreamState.cs
public enum NexusStreamState
{
    None,
    Opening,
    Open,
    Closed
}
```

#### 2.2 Stream Transport (Skeleton)

```csharp
// Interfaces/INexusStreamTransport.cs
public interface INexusStreamTransport : IAsyncDisposable
{
    Task ReadyTask { get; }

    /// <summary>
    /// Opens a stream. Throws InvalidOperationException if a stream is already open.
    /// </summary>
    /// <param name="resumePosition">Position to resume from, or -1 for fresh start (implemented in Phase 5)</param>
    ValueTask<INexusStream> OpenAsync(
        string resourceId,
        StreamAccessMode access,
        StreamShareMode share = StreamShareMode.None,
        long resumePosition = -1,
        CancellationToken ct = default);

    IAsyncEnumerable<INexusStreamRequest> ReceiveRequest(CancellationToken ct = default);

    ValueTask ProvideFile(string path, CancellationToken ct = default);
    ValueTask ProvideStream(Stream stream, CancellationToken ct = default);
}
```

```csharp
// NexusStreamTransport.cs
internal sealed class NexusStreamTransport : INexusStreamTransport
{
    private readonly INexusDuplexPipe _pipe;
    private readonly NexusStreamFrameWriter _writer;
    private readonly NexusStreamFrameReader _reader;
    private NexusStreamState _state;

    // State transitions
    private void TransitionTo(NexusStreamState newState);
    private void ValidateTransition(NexusStreamState from, NexusStreamState to);

    // Phase 2: Open/Close only
    public async ValueTask<INexusStream> OpenAsync(...);
    public async IAsyncEnumerable<INexusStreamRequest> ReceiveRequest(...);

    // Stub for Phase 6
    public ValueTask ProvideFile(string path, CancellationToken ct) => throw new NotImplementedException();
    public ValueTask ProvideStream(Stream stream, CancellationToken ct) => throw new NotImplementedException();
}
```

#### 2.3 Stream Request

```csharp
// Interfaces/INexusStreamRequest.cs
public interface INexusStreamRequest
{
    string ResourceId { get; }
    StreamAccessMode Access { get; }
    StreamShareMode Share { get; }
    long ResumePosition { get; }
}

// NexusStreamRequest.cs
internal sealed class NexusStreamRequest : INexusStreamRequest
{
    // Implementation
}
```

#### 2.4 Stream Implementation (Skeleton)

```csharp
// Interfaces/INexusStream.cs
public interface INexusStream : IAsyncDisposable
{
    NexusStreamState State { get; }
    Exception? Error { get; }
    long Position { get; }
    long Length { get; }
    bool HasKnownLength { get; }
    bool CanSeek { get; }
    bool CanRead { get; }
    bool CanWrite { get; }

    // Phase 3
    ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default);
    ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default);

    // Phase 4
    ValueTask<long> SeekAsync(long offset, SeekOrigin origin, CancellationToken ct = default);
    ValueTask FlushAsync(CancellationToken ct = default);
    ValueTask SetLengthAsync(long length, CancellationToken ct = default);

    // Phase 5
    ValueTask<NexusStreamMetadata> GetMetadataAsync(CancellationToken ct = default);
    IObservable<NexusStreamProgress> Progress { get; }

    // Phase 6
    Stream GetStream();
}

// NexusStream.cs
internal sealed class NexusStream : INexusStream
{
    private NexusStreamState _state;
    private Exception? _error;

    // Phase 2: State and lifecycle only
    // Other methods throw NotImplementedException until their phase
}
```

#### 2.5 OpenResponse Frame

```csharp
// Frames/OpenResponseFrame.cs
public readonly struct OpenResponseFrame
{
    public bool Success { get; init; }
    public int ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public NexusStreamMetadata? Metadata { get; init; }  // Simplified in Phase 2

    // Serialization methods
}
```

### Phase 2 Tests

| Test Class | Coverage |
|------------|----------|
| `StateMachineTests` | All valid transitions, invalid transition rejection |
| `OpenResponseFrameTests` | Success/failure serialization, metadata presence |
| `NexusStreamTransportTests` | OpenAsync flow, ReceiveRequest enumeration |
| `LifecycleTests` | Client-initiated flow, server-initiated flow, sequential streams |
| `ConcurrentOpenTests` | Second OpenAsync throws InvalidOperationException |
| `TransportReuseTests` | Transport reusable after stream error (not protocol error) |

### Phase 2 Deliverables

- [ ] `NexusStreamState` enum
- [ ] `INexusStreamTransport` interface
- [ ] `NexusStreamTransport` implementation (open/close only)
- [ ] `INexusStreamRequest` interface and implementation
- [ ] `INexusStream` interface
- [ ] `NexusStream` skeleton implementation
- [ ] `OpenResponseFrame` struct
- [ ] State machine with transition validation
- [ ] Unit tests for state machine and lifecycle

---

## Phase 3: Data Transfer

**Goal:** Implement read/write operations with data frames.

### Components

#### 3.1 Data Transfer Frames

```csharp
// Frames/ReadFrame.cs
public readonly struct ReadFrame
{
    public int Count { get; init; }
    // ...
}

// Frames/WriteFrame.cs
public readonly struct WriteFrame
{
    public int Count { get; init; }
    // ...
}

// Frames/WriteResponseFrame.cs
public readonly struct WriteResponseFrame
{
    public bool Success { get; init; }
    public int BytesWritten { get; init; }
    public long Position { get; init; }
    public int ErrorCode { get; init; }
    // ...
}

// Frames/DataFrame.cs
public readonly struct DataFrame
{
    public uint Sequence { get; init; }
    public ReadOnlyMemory<byte> Data { get; init; }
    // ...
}

// Frames/DataEndFrame.cs
public readonly struct DataEndFrame
{
    public int TotalBytes { get; init; }
    public uint FinalSequence { get; init; }
    // ...
}
```

#### 3.2 Sequence Number Manager

```csharp
// Internal helper for sequence validation
internal sealed class SequenceManager
{
    private uint _nextExpected;
    private uint _nextToSend;

    public uint GetNextSendSequence();
    public bool ValidateReceived(uint sequence);
    public void Reset();
}
```

#### 3.3 Stream Read/Write Implementation

**Design Notes:**
- **Partial reads:** Return available bytes immediately (like `System.IO.Stream`). If 500 bytes available when 1000 requested, return 500.
- **Cancellation:** When cancelled, drain pending Data frames to keep pipe in sync before throwing `OperationCanceledException`.
- **Access validation:** Check CanRead/CanWrite locally before sending frames; throw `NotSupportedException` if invalid.
- **Extra data:** If remote sends more data than requested, treat as protocol error and disconnect.

```csharp
// NexusStream.cs additions
internal sealed partial class NexusStream
{
    private readonly SemaphoreSlim _readLock = new(1, 1);
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly SequenceManager _readSequence = new();
    private readonly SequenceManager _writeSequence = new();

    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
    {
        if (!CanRead)
            throw new NotSupportedException("Stream does not support reading.");

        await _readLock.WaitAsync(ct);
        try
        {
            // Send Read frame with buffer.Length
            // Receive Data frames until DataEnd
            // Validate total bytes <= requested (protocol error if exceeded)
            // Return as soon as any data available (partial read OK)
            // Validate sequences
            // Copy to buffer
            // Update position
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Drain any pending frames to keep pipe in sync
            await DrainPendingDataFramesAsync();
            throw;
        }
        finally
        {
            _readLock.Release();
        }
    }

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        if (!CanWrite)
            throw new NotSupportedException("Stream does not support writing.");

        await _writeLock.WaitAsync(ct);
        try
        {
            // Send Write frame with data.Length
            // Send Data frames (chunked if needed)
            // Send DataEnd frame
            // Wait for WriteResponse
            // Update position from response
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Drain any pending response frames
            await DrainPendingResponseAsync();
            throw;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async ValueTask DrainPendingDataFramesAsync() { /* ... */ }
    private async ValueTask DrainPendingResponseAsync() { /* ... */ }
}
```

#### 3.4 Frame Writer Extensions

```csharp
// NexusStreamFrameWriter.cs additions
internal sealed partial class NexusStreamFrameWriter
{
    public ValueTask WriteReadAsync(ReadFrame frame, CancellationToken ct);
    public ValueTask WriteWriteAsync(WriteFrame frame, CancellationToken ct);
    public ValueTask WriteWriteResponseAsync(WriteResponseFrame frame, CancellationToken ct);
    public ValueTask WriteDataAsync(DataFrame frame, CancellationToken ct);
    public ValueTask WriteDataEndAsync(DataEndFrame frame, CancellationToken ct);

    // Chunked data writing for large payloads
    public async IAsyncEnumerable<DataFrame> ChunkDataAsync(
        ReadOnlyMemory<byte> data,
        [EnumeratorCancellation] CancellationToken ct);
}
```

#### 3.5 Frame Reader Extensions

```csharp
// NexusStreamFrameReader.cs additions
internal sealed partial class NexusStreamFrameReader
{
    public static ReadFrame ParseRead(ReadOnlySpan<byte> payload);
    public static WriteFrame ParseWrite(ReadOnlySpan<byte> payload);
    public static WriteResponseFrame ParseWriteResponse(ReadOnlySpan<byte> payload);
    public static DataFrame ParseData(ReadOnlySpan<byte> payload);
    public static DataEndFrame ParseDataEnd(ReadOnlySpan<byte> payload);
}
```

### Phase 3 Tests

| Test Class | Coverage |
|------------|----------|
| `ReadFrameTests` | Count serialization, boundary values |
| `WriteFrameTests` | Count serialization |
| `WriteResponseFrameTests` | Success/failure, position updates |
| `DataFrameTests` | Sequence numbers, payload handling |
| `DataEndFrameTests` | Total bytes, final sequence |
| `SequenceManagerTests` | Sequential validation, gap detection, reset |
| `NexusStreamReadTests` | Single read, multiple reads, partial reads, empty reads |
| `NexusStreamWriteTests` | Single write, chunked writes, write errors |
| `DataChunkingTests` | Payloads larger than max size, boundary conditions |
| `AccessValidationTests` | Read on write-only throws, write on read-only throws |
| `ExtraDataProtocolErrorTests` | More data than requested triggers disconnect |

### Phase 3 Deliverables

- [ ] `ReadFrame`, `WriteFrame`, `WriteResponseFrame` structs
- [ ] `DataFrame`, `DataEndFrame` structs
- [ ] `SequenceManager` for sequence validation
- [ ] `NexusStream.ReadAsync` implementation
- [ ] `NexusStream.WriteAsync` implementation
- [ ] Frame writer extensions for data frames
- [ ] Frame reader extensions for data frames
- [ ] Data chunking for large payloads
- [ ] Unit tests for all data transfer scenarios

---

## Phase 4: Navigation & Synchronization

**Goal:** Implement seek, flush, position synchronization, and concurrency.

### Components

#### 4.1 Navigation Frames

```csharp
// Frames/SeekFrame.cs
public readonly struct SeekFrame
{
    public long Offset { get; init; }
    public SeekOrigin Origin { get; init; }  // 0=Begin, 1=Current, 2=End
    // ...
}

// Frames/SeekResponseFrame.cs
public readonly struct SeekResponseFrame
{
    public bool Success { get; init; }
    public long Position { get; init; }
    public int ErrorCode { get; init; }
    // ...
}

// Frames/FlushFrame.cs - empty payload
public readonly struct FlushFrame { }

// Frames/FlushResponseFrame.cs
public readonly struct FlushResponseFrame
{
    public bool Success { get; init; }
    public int ErrorCode { get; init; }
    // ...
}

// Frames/SetLengthFrame.cs
public readonly struct SetLengthFrame
{
    public long Length { get; init; }
    // ...
}

// Frames/SetLengthResponseFrame.cs
public readonly struct SetLengthResponseFrame
{
    public bool Success { get; init; }
    public int ErrorCode { get; init; }
    // ...
}
```

#### 4.2 Position Tracking

```csharp
// NexusStream.cs additions
internal sealed partial class NexusStream
{
    private long _position;
    private readonly bool _canSeek;

    public long Position => _canSeek ? _position : throw new NotSupportedException();

    public async ValueTask<long> SeekAsync(long offset, SeekOrigin origin, CancellationToken ct)
    {
        if (!_canSeek)
            throw new NotSupportedException("Stream does not support seeking.");

        // Acquire BOTH locks for exclusive access
        await _readLock.WaitAsync(ct);
        try
        {
            await _writeLock.WaitAsync(ct);
            try
            {
                // Send Seek frame
                // Wait for SeekResponse
                // Update _position from response (authoritative)
                return _position;
            }
            finally
            {
                _writeLock.Release();
            }
        }
        finally
        {
            _readLock.Release();
        }
    }

    public async ValueTask FlushAsync(CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            // Send Flush frame
            // Wait for FlushResponse
            // Handle errors
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async ValueTask SetLengthAsync(long length, CancellationToken ct)
    {
        if (!_canWrite)
            throw new NotSupportedException("Stream does not support writing.");

        // Acquire both locks for exclusive access (like Seek)
        await _readLock.WaitAsync(ct);
        try
        {
            await _writeLock.WaitAsync(ct);
            try
            {
                // Send SetLength frame
                // Wait for SetLengthResponse
                // Update local Length property if successful
            }
            finally
            {
                _writeLock.Release();
            }
        }
        finally
        {
            _readLock.Release();
        }
    }
}
```

#### 4.3 Concurrent Read/Write Support

The locking strategy from spec §10.2:
- Read and write use separate locks (full-duplex capable)
- Seek acquires both locks (exclusive)
- Non-seekable streams don't need position coordination

```csharp
// Document the concurrency model clearly
// Read: acquires _readLock only
// Write: acquires _writeLock only
// Seek: acquires _readLock then _writeLock
// Flush: acquires _writeLock only
```

#### 4.4 Non-Seekable Stream Handling

```csharp
internal sealed partial class NexusStream
{
    // For non-seekable streams:
    // - Position property throws NotSupportedException
    // - SeekAsync throws NotSupportedException
    // - Read/Write operate without position tracking
    // - Sequence numbers still validate frame ordering
}
```

### Phase 4 Tests

| Test Class | Coverage |
|------------|----------|
| `SeekFrameTests` | All origins, positive/negative offsets |
| `SeekResponseFrameTests` | Success/failure, position values |
| `FlushFrameTests` | Empty payload serialization |
| `FlushResponseFrameTests` | Success/failure codes |
| `SetLengthFrameTests` | Length serialization, boundary values |
| `SetLengthResponseFrameTests` | Success/failure codes |
| `PositionSynchronizationTests` | Position after read, write, seek; server authority |
| `ConcurrencyTests` | Concurrent reads, concurrent writes, read+write parallel |
| `SeekLockingTests` | Seek blocks read/write, proper lock ordering |
| `SetLengthTests` | Truncate, extend, non-writable stream throws |
| `NonSeekableStreamTests` | Position throws, seek throws, data still flows |

### Phase 4 Deliverables

- [ ] `SeekFrame`, `SeekResponseFrame` structs
- [ ] `FlushFrame`, `FlushResponseFrame` structs
- [ ] `SetLengthFrame`, `SetLengthResponseFrame` structs
- [ ] `NexusStream.SeekAsync` implementation
- [ ] `NexusStream.FlushAsync` implementation
- [ ] `NexusStream.SetLengthAsync` implementation
- [ ] Position tracking with server authority
- [ ] Concurrent read/write locking strategy
- [ ] Non-seekable stream support
- [ ] Frame writer/reader extensions
- [ ] Unit tests for navigation, SetLength, and concurrency

---

## Phase 5: Metadata & Progress

**Goal:** Implement metadata, progress notifications, and stream resumption.

### Components

#### 5.1 Metadata Structures

```csharp
// NexusStreamMetadata.cs
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

    // Wire format per spec §7.1
    internal byte GetFlags();
    internal static NexusStreamMetadata FromWireFormat(ReadOnlySpan<byte> buffer);
    internal int WriteToBuffer(Span<byte> buffer);
}

// Frames/GetMetadataFrame.cs - empty payload
public readonly struct GetMetadataFrame { }

// Frames/MetadataResponseFrame.cs
public readonly struct MetadataResponseFrame
{
    public NexusStreamMetadata Metadata { get; init; }
    // ...
}
```

#### 5.2 Progress Tracking

```csharp
// NexusStreamProgress.cs
public readonly struct NexusStreamProgress
{
    public long BytesRead { get; init; }        // Total bytes read
    public long BytesWritten { get; init; }     // Total bytes written
    public long TotalReadBytes { get; init; }   // Expected read total (-1 if unknown)
    public long TotalWriteBytes { get; init; }  // Expected write total (-1 if unknown)
    public TimeSpan Elapsed { get; init; }
    public double ReadBytesPerSecond { get; init; }
    public double WriteBytesPerSecond { get; init; }
    public TransferState State { get; init; }
}

// Enums/TransferState.cs
public enum TransferState : byte
{
    Active = 0,
    Paused = 1,
    Complete = 2,
    Failed = 3
}

// Frames/ProgressFrame.cs
public readonly struct ProgressFrame
{
    public long BytesTransferred { get; init; }
    public long TotalBytes { get; init; }
    public long ElapsedTicks { get; init; }
    public double BytesPerSecond { get; init; }
    public TransferState State { get; init; }
    // ...
}
```

#### 5.3 Progress Observable

```csharp
// Internal progress subject
internal sealed class ProgressSubject : IObservable<NexusStreamProgress>, IDisposable
{
    private readonly List<IObserver<NexusStreamProgress>> _observers = new();

    public void OnNext(NexusStreamProgress progress);
    public void OnCompleted();
    public void OnError(Exception error);
    public IDisposable Subscribe(IObserver<NexusStreamProgress> observer);
}

// NexusStream.cs additions
internal sealed partial class NexusStream
{
    private readonly ProgressSubject _progressSubject = new();
    private readonly Stopwatch _elapsed = new();
    private long _bytesRead;
    private long _bytesWritten;

    public IObservable<NexusStreamProgress> Progress => _progressSubject;

    // Called periodically during transfers
    private void EmitProgress(TransferState state);
}
```

#### 5.4 Progress Notification Triggers

Per spec §8.2:
- Byte threshold: configurable (default 1MB)
- Time interval: configurable (default 5 seconds)
- State change: pause, resume, complete, fail

```csharp
internal sealed class ProgressTracker
{
    private readonly long _byteThreshold;
    private readonly TimeSpan _timeInterval;
    private long _lastReportedBytes;
    private DateTime _lastReportedTime;

    public bool ShouldReport(long currentBytes, TransferState state);
}
```

#### 5.5 Resume Functionality

```csharp
// OpenFrame already has ResumePosition field (included since Phase 2)
// Server-side handling:
internal sealed partial class NexusStreamTransport
{
    private async ValueTask HandleOpenRequest(OpenFrame frame)
    {
        if (frame.ResumePosition >= 0)
        {
            // Seek underlying stream to resume position
            // Validate position is valid (return error if beyond EOF)
            // Send metadata with current position
        }
    }
}

// Client-side: OpenAsync signature already includes resumePosition since Phase 2
// Phase 5 implements the actual resume logic
```

### Phase 5 Tests

| Test Class | Coverage |
|------------|----------|
| `NexusStreamMetadataTests` | All flags, wire format, optional fields |
| `GetMetadataFrameTests` | Empty payload handling |
| `MetadataResponseFrameTests` | Full metadata serialization |
| `ProgressFrameTests` | All fields, unknown length handling |
| `ProgressSubjectTests` | Subscribe, unsubscribe, completion, errors |
| `ProgressTrackerTests` | Byte threshold, time interval, state changes |
| `ProgressTests` | Progress during read, write, accurate rates |
| `ResumptionTests` | Resume from position, invalid position handling |

### Phase 5 Deliverables

- [ ] `NexusStreamMetadata` struct with wire format
- [ ] `GetMetadataFrame`, `MetadataResponseFrame` structs
- [ ] `NexusStreamProgress` struct (with separate read/write counters)
- [ ] `TransferState` enum
- [ ] `ProgressFrame` struct
- [ ] `ProgressSubject` observable implementation
- [ ] `ProgressTracker` for notification timing
- [ ] `GetMetadataAsync` implementation
- [ ] Progress observable on `INexusStream`
- [ ] Resume position handling in server-side `HandleOpenRequest`
- [ ] Unit tests for metadata, progress, resumption

---

## Phase 6: Integration & Polish

**Goal:** Complete integration with NexNet, System.IO.Stream wrapper, Ack frames, and end-to-end testing.

### Components

#### 6.1 System.IO.Stream Wrapper

> **Note:** Sync methods use sync-over-async pattern (`.GetAwaiter().GetResult()`).
> Prefer async methods to avoid potential thread pool starvation. Document this limitation.

```csharp
// NexusStreamWrapper.cs
public sealed class NexusStreamWrapper : Stream
{
    private readonly INexusStream _inner;

    public NexusStreamWrapper(INexusStream stream);

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => _inner.CanSeek;
    public override bool CanWrite => _inner.CanWrite;
    public override long Length => _inner.Length;

    public override long Position
    {
        get => _inner.Position;
        set => _inner.SeekAsync(value, SeekOrigin.Begin).AsTask().GetAwaiter().GetResult();
    }

    public override int Read(byte[] buffer, int offset, int count)
        => ReadAsync(buffer, offset, count).GetAwaiter().GetResult();

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        => await _inner.ReadAsync(buffer.AsMemory(offset, count), ct);

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        => await _inner.ReadAsync(buffer, ct);

    // ... Write, Seek, Flush implementations

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _inner.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    public override async ValueTask DisposeAsync()
        => await _inner.DisposeAsync();
}

// INexusStream.cs addition
public interface INexusStream
{
    Stream GetStream();  // Returns NexusStreamWrapper
}
```

#### 6.2 Ack Frames (Optional Flow Control)

```csharp
// Frames/AckFrame.cs
public readonly struct AckFrame
{
    public uint AckedSequence { get; init; }
    public uint WindowSize { get; init; }
    // ...
}

// Sliding window manager
internal sealed class SlidingWindowManager
{
    private readonly uint _windowSize;
    private uint _lastAcked;
    private uint _lastSent;

    public bool CanSend => (_lastSent - _lastAcked) < _windowSize;
    public void OnAck(uint sequence, uint newWindowSize);
    public uint GetNextSequence();
}
```

> **Note:** Ack frames provide fine-grained flow control on top of pipe back-pressure. Implementation is optional for Phase 6 if existing water marks suffice.

#### 6.3 ProvideFile / ProvideStream Helpers

```csharp
// NexusStreamTransport.cs completion
internal sealed partial class NexusStreamTransport
{
    public async ValueTask ProvideFile(string path, CancellationToken ct = default)
    {
        await using var fileStream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.Read,
            bufferSize: 4096,
            useAsync: true);

        await ProvideStream(fileStream, ct);
    }

    public async ValueTask ProvideStream(Stream stream, CancellationToken ct = default)
    {
        // Build metadata from stream
        var metadata = new NexusStreamMetadata
        {
            Length = stream.CanSeek ? stream.Length : -1,
            HasKnownLength = stream.CanSeek,
            CanSeek = stream.CanSeek,
            CanRead = stream.CanRead,
            CanWrite = stream.CanWrite,
            // File-specific metadata if FileStream
        };

        // Send OpenResponse with metadata
        // Handle incoming frames until Close received
        // Translate frames to stream operations
    }
}
```

#### 6.4 Session Integration

```csharp
// Extension to NexusClient / SessionContext
public interface IRentedNexusStreamTransport : INexusStreamTransport
{
    // Returned to pool on dispose
}

// On INexusClient<TProxy>
public IRentedNexusStreamTransport CreateStream();

// On SessionContext<TProxy>
public IRentedNexusStreamTransport CreateStream();
```

#### 6.5 Error Handling Polish

- Ensure all error codes from spec §9.1 are properly mapped
- Sanitize error messages per spec §13.4
- Validate protocol error disconnection behavior

### Phase 6 Tests

| Test Class | Coverage |
|------------|----------|
| `NexusStreamWrapperTests` | All Stream methods, sync/async, dispose |
| `AckFrameTests` | Sequence/window serialization |
| `SlidingWindowManagerTests` | Window management, ack handling |
| `ProvideFileTests` | File metadata, read/write operations |
| `ProvideStreamTests` | Generic stream metadata, operations |
| `FileTransferTests` | End-to-end file transfer, large files |
| `GenericStreamTests` | MemoryStream, NetworkStream wrapping |
| `EndToEndTests` | Full client-server scenarios |
| `ErrorHandlingTests` | All error codes, protocol error disconnection |
| `SecurityTests` | Resource ID validation, path traversal prevention |
| `SessionDisconnectTests` | Pending operations throw on disconnect |

### Phase 6 Deliverables

- [ ] `NexusStreamWrapper` : Stream implementation
- [ ] `GetStream()` on INexusStream
- [ ] `AckFrame` struct (optional)
- [ ] `SlidingWindowManager` (optional)
- [ ] `ProvideFile` implementation
- [ ] `ProvideStream` implementation
- [ ] `CreateStream()` on client and session context
- [ ] Full error code mapping
- [ ] Error message sanitization
- [ ] End-to-end integration tests
- [ ] Performance benchmarks
- [ ] Documentation updates

---

## Future Phases (Post-6)

Per spec §14, these are reserved for future implementation:

### Phase 7: Compression

- Compressed data frame (0x90 = 0x10 | 0x80)
- Compression negotiation frames (0x70-0x7F)
- Configurable compression algorithms

### Phase 8: Locking

- Lock/LockResponse frames (0x50-0x5F)
- Exclusive and shared byte-range locks
- Lease-based with configurable timeout
- OS-level locking integration for files

### Phase 9: Metadata Watching

- Watch/WatchResponse/MetadataChanged/Unwatch frames (0x60-0x6F)
- Push-based metadata updates
- File system watcher integration

---

## Testing Strategy

### Unit Test Coverage Requirements

| Phase | Minimum Coverage |
|-------|------------------|
| 1 | 95% (frame serialization is critical) |
| 2 | 90% (state machine must be correct) |
| 3 | 90% (data integrity) |
| 4 | 85% (concurrency edge cases) |
| 5 | 85% (progress timing) |
| 6 | 80% (integration focus) |

### Test Categories

1. **Unit Tests:** Individual component testing
2. **Integration Tests:** Component interaction
3. **End-to-End Tests:** Full client-server scenarios
4. **Performance Tests:** Throughput, latency, memory
5. **Stress Tests:** Concurrent connections, large files

### Test Infrastructure

```csharp
// Shared test utilities
internal static class StreamTestHelpers
{
    public static (INexusDuplexPipe Client, INexusDuplexPipe Server) CreatePipePair();
    public static NexusStreamTransport CreateTransport(INexusDuplexPipe pipe);
    public static byte[] GenerateTestData(int size);
    public static void AssertFrameEqual(ReadOnlySpan<byte> expected, ReadOnlySpan<byte> actual);
}
```

---

## Dependencies & Prerequisites

### Before Phase 1

- [ ] Review existing `INexusDuplexPipe` implementation
- [ ] Confirm pipe water mark behavior
- [ ] Set up test project structure

### Cross-Phase Dependencies

| Phase | Depends On |
|-------|------------|
| 2 | 1 (frames) |
| 3 | 2 (lifecycle) |
| 4 | 3 (data transfer) |
| 5 | 4 (position tracking) |
| 6 | 1-5 (all) |

---

## Revision History

| Version | Date | Changes |
|---------|------|---------|
| 1.0 | 2024-XX-XX | Initial 6-phase plan |
