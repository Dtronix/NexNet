using System.Diagnostics;
using NexNet.Inspector.Core.Model;
using NexNet.Messages;

namespace NexNet.Inspector.Core.Format;

/// <summary>Body of the header block: identifies the file and anchors Stopwatch ticks to wall-clock time.</summary>
/// <param name="FormatVersion">Format major version; see <see cref="NntlFormat.Version"/>.</param>
/// <param name="WallClockUtcTicks">UTC <see cref="DateTime.Ticks"/> taken at the anchor.</param>
/// <param name="StopwatchFrequency"><see cref="Stopwatch.Frequency"/> of the recording machine.</param>
/// <param name="StopwatchTicksAtAnchor"><see cref="Stopwatch.GetTimestamp"/> taken at the anchor.</param>
/// <param name="Producer">Name of the program that wrote the file.</param>
/// <param name="ProducerVersion">Version of that program.</param>
/// <param name="Host">Machine name, when known.</param>
public readonly record struct NntlFileHeader(
    int FormatVersion,
    long WallClockUtcTicks,
    long StopwatchFrequency,
    long StopwatchTicksAtAnchor,
    string Producer,
    string ProducerVersion,
    string? Host)
{
    /// <summary>Creates a header anchored at the current time on this machine.</summary>
    public static NntlFileHeader Create(string producer, string producerVersion, string? host = null)
    {
        // Read the two clocks back to back so the anchor pairs them as closely as possible.
        var ticks = Stopwatch.GetTimestamp();
        var utc = DateTime.UtcNow.Ticks;
        return new NntlFileHeader(NntlFormat.Version, utc, Stopwatch.Frequency, ticks, producer, producerVersion, host);
    }

    /// <summary>Converts recorded Stopwatch ticks to UTC wall-clock time through the anchor.</summary>
    public DateTime ToUtc(long stopwatchTicks)
    {
        var delta = stopwatchTicks - StopwatchTicksAtAnchor;
        var dateTicks = StopwatchFrequency == TimeSpan.TicksPerSecond
            ? delta
            : (long)(delta * ((double)TimeSpan.TicksPerSecond / StopwatchFrequency));
        return new DateTime(WallClockUtcTicks + dateTicks, DateTimeKind.Utc);
    }

    /// <summary>Converts a span of recorded Stopwatch ticks to a <see cref="TimeSpan"/>.</summary>
    public TimeSpan ToTimeSpan(long stopwatchTicks) =>
        TimeSpan.FromSeconds(stopwatchTicks / (double)StopwatchFrequency);
}

/// <summary>Body of a session-open block.</summary>
/// <param name="SessionId">Id that every later block of this session carries.</param>
/// <param name="Role">Server or client side.</param>
/// <param name="Ticks">Stopwatch ticks when recording of the session began.</param>
/// <param name="TransportName">Transport display name (for example <c>Tcp</c>).</param>
/// <param name="EndpointName">Server endpoint name, when known.</param>
/// <param name="LocalEndpoint">Local address, when known.</param>
/// <param name="RemoteEndpoint">Remote address, when known.</param>
/// <param name="SchemaHash">Hash of the schema block that decodes this session; empty when none.</param>
public readonly record struct NntlSessionOpen(
    long SessionId,
    NntlSessionRole Role,
    long Ticks,
    string TransportName,
    string? EndpointName,
    string? LocalEndpoint,
    string? RemoteEndpoint,
    ReadOnlyMemory<byte> SchemaHash);

/// <summary>Body of a schema block.</summary>
/// <param name="Hash">Hash identifying the schema; session-open blocks refer to it.</param>
/// <param name="Schema">The <c>.nnschema</c> bytes, opaque to the format.</param>
public readonly record struct NntlSchema(ReadOnlyMemory<byte> Hash, ReadOnlyMemory<byte> Schema);

/// <summary>A byte range inside a transport op.</summary>
/// <param name="Start">Offset of the first byte.</param>
/// <param name="Length">Number of bytes.</param>
public readonly record struct NntlByteRange(int Start, int Length);

/// <summary>Body of a transport op block: one raw read or write.</summary>
/// <param name="SessionId">Owning session.</param>
/// <param name="Direction">Read (inbound) or write (outbound).</param>
/// <param name="OpId">Sequence number of the op, as assigned by the tap.</param>
/// <param name="Ticks">Stopwatch ticks when the op happened.</param>
/// <param name="Flags">Op flags.</param>
/// <param name="Redactions">Ranges of <paramref name="Bytes"/> that were replaced before recording.</param>
/// <param name="Bytes">The bytes moved by the op.</param>
public readonly record struct NntlTransportOp(
    long SessionId,
    TapDirection Direction,
    long OpId,
    long Ticks,
    NntlTransportOpFlags Flags,
    ReadOnlyMemory<NntlByteRange> Redactions,
    ReadOnlyMemory<byte> Bytes);

/// <summary>Body of a gap block: ops the recorder had to drop.</summary>
/// <param name="SessionId">Owning session.</param>
/// <param name="Direction">Direction of the dropped ops.</param>
/// <param name="FromTicks">Ticks of the first dropped op.</param>
/// <param name="ToTicks">Ticks of the last dropped op.</param>
/// <param name="DroppedOps">Number of ops dropped.</param>
/// <param name="DroppedBytes">Number of bytes those ops carried.</param>
public readonly record struct NntlGap(
    long SessionId,
    TapDirection Direction,
    long FromTicks,
    long ToTicks,
    long DroppedOps,
    long DroppedBytes);

/// <summary>One section of a snapshot.</summary>
/// <param name="Kind">What the section describes.</param>
/// <param name="Data">MessagePack payload whose layout belongs to the section kind.</param>
public readonly record struct NntlSnapshotSection(NntlSnapshotSectionKind Kind, ReadOnlyMemory<byte> Data);

/// <summary>Body of a snapshot block.</summary>
/// <param name="SessionId">Owning session.</param>
/// <param name="Ticks">Stopwatch ticks when the snapshot was taken.</param>
/// <param name="Version">Snapshot model version; see <see cref="CurrentVersion"/>.</param>
/// <param name="Sections">The sections, in the order written.</param>
public readonly record struct NntlSnapshot(
    long SessionId,
    long Ticks,
    int Version,
    ReadOnlyMemory<NntlSnapshotSection> Sections)
{
    /// <summary>Snapshot model version this build writes.</summary>
    public const int CurrentVersion = 1;
}

/// <summary>Body of a session-close block.</summary>
/// <param name="SessionId">Owning session.</param>
/// <param name="Ticks">Stopwatch ticks when recording of the session ended.</param>
/// <param name="Kind">Disconnected, or recording stopped.</param>
/// <param name="DisconnectReason">NexNet's reason when <paramref name="Kind"/> is <see cref="NntlCloseKind.Disconnected"/>.</param>
public readonly record struct NntlSessionClose(
    long SessionId,
    long Ticks,
    NntlCloseKind Kind,
    DisconnectReason DisconnectReason);

/// <summary>A block read from a file: its kind, where it starts and its body.</summary>
/// <param name="Offset">Offset of the block's first byte (its sync marker).</param>
/// <param name="Kind">Block kind.</param>
/// <param name="Body">MessagePack body. Owned by the reader and valid until its next read.</param>
public readonly record struct NntlBlock(long Offset, NntlBlockKind Kind, ReadOnlyMemory<byte> Body)
{
    /// <summary>Total size of the block on disk, header included.</summary>
    public long Length => NntlFormat.BlockHeaderSize + Body.Length;
}

/// <summary>A region of a file the reader could not use.</summary>
/// <param name="Kind">What went wrong.</param>
/// <param name="Offset">First byte of the region.</param>
/// <param name="Length">Size of the region in bytes.</param>
public readonly record struct NntlDamage(NntlDamageKind Kind, long Offset, long Length);

/// <summary>One indexed block.</summary>
/// <param name="Offset">Offset of the block.</param>
/// <param name="Ticks">The block's primary timestamp (see the format spec); 0 for schema blocks.</param>
public readonly record struct NntlIndexEntry(long Offset, long Ticks);

/// <summary>One indexed block with its group key, for listing blocks in file order.</summary>
/// <param name="Offset">Offset of the block.</param>
/// <param name="Kind">Block kind.</param>
/// <param name="SessionId">Owning session; 0 for header and schema blocks.</param>
/// <param name="Ticks">The block's primary timestamp.</param>
public readonly record struct NntlBlockRef(long Offset, NntlBlockKind Kind, long SessionId, long Ticks);

/// <summary>Outcome of <see cref="NntlFile.RebuildIndex"/>.</summary>
/// <param name="BlockCount">Blocks in the rebuilt index.</param>
/// <param name="TruncatedBytes">Bytes removed from the end (torn tail and any old footer).</param>
/// <param name="Damage">Damage found while scanning.</param>
public readonly record struct NntlRebuildResult(long BlockCount, long TruncatedBytes, IReadOnlyList<NntlDamage> Damage);
