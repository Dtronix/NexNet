using System.Buffers;
using NexNet.Collections;
using NexNet.Diagnostics;
using NexNet.Inspector.Core.Model;
using NexNet.Serialization;

namespace NexNet.Inspector.Core.Format;

/// <summary>State flags of a pipe in an open-pipes snapshot section.</summary>
[Flags]
public enum NntlPipeStateFlags : byte
{
    /// <summary>No flag.</summary>
    None = 0,

    /// <summary>Both sides set the pipe up.</summary>
    Ready = 1,

    /// <summary>The snapshotting side's reader completed.</summary>
    ReaderCompleted = 2,

    /// <summary>The snapshotting side's writer completed.</summary>
    WriterCompleted = 4,

    /// <summary>The snapshotting side sent its Complete and waits for the peer to confirm.</summary>
    Closing = 8,
}

/// <summary>One pipe of an open-pipes section (<see cref="NntlSnapshotSectionKind.OpenPipes"/>).</summary>
/// <param name="Id">Pipe ID as on the wire; until the peer acknowledges a pipe this side opened, only this side's half is set.</param>
/// <param name="Flags">Lifecycle flags.</param>
/// <param name="Window">Receive window, in bytes.</param>
/// <param name="ReceiveCredit">Bytes the peer may still send.</param>
/// <param name="Buffered">Bytes received and not yet read by the application.</param>
/// <param name="SendCredit">Bytes this side may still send.</param>
public readonly record struct NntlPipeState(
    ushort Id,
    NntlPipeStateFlags Flags,
    int Window,
    long ReceiveCredit,
    long Buffered,
    long SendCredit);

/// <summary>An argument or result larger than one frame that was in transfer when the snapshot was taken.</summary>
/// <param name="Direction">Inbound when the snapshotting side receives it.</param>
/// <param name="IsResult">True for an invocation result, false for invocation arguments.</param>
/// <param name="InvocationId">ID of the invocation it belongs to.</param>
/// <param name="Length">Total length, in bytes.</param>
/// <param name="Transferred">Bytes transferred so far.</param>
public readonly record struct NntlLargeMessageState(
    TapDirection Direction,
    bool IsResult,
    ushort InvocationId,
    long Length,
    long Transferred);

/// <summary>The pending-invocations section (<see cref="NntlSnapshotSectionKind.PendingInvocations"/>).</summary>
/// <remarks>
/// NexNet's session snapshot counts invocations but does not list their IDs, so this section carries the counts plus
/// the large messages in transfer, which do carry invocation IDs.
/// </remarks>
/// <param name="Queued">Invocations received and waiting for an execution slot.</param>
/// <param name="Executing">Invocations running.</param>
/// <param name="AwaitingResults">Calls this side made that wait for their result.</param>
/// <param name="InboundCredits">Invocations the peer may still queue on this side.</param>
/// <param name="OutboundCredits">Invocations this side may still send.</param>
/// <param name="CreditWaits">Calls waiting for the peer to return credit.</param>
/// <param name="SeparatePoolQueued">Separate-pool invocations waiting for a slot.</param>
/// <param name="SeparatePoolExecuting">Separate-pool invocations running.</param>
/// <param name="LargeMessages">Arguments and results larger than one frame in transfer.</param>
public readonly record struct NntlInvocationState(
    int Queued,
    int Executing,
    int AwaitingResults,
    int InboundCredits,
    int OutboundCredits,
    int CreditWaits,
    int SeparatePoolQueued,
    int SeparatePoolExecuting,
    NntlLargeMessageState[] LargeMessages);

/// <summary>One synchronized collection of a collection-state section (<see cref="NntlSnapshotSectionKind.CollectionState"/>).</summary>
/// <param name="Id">Collection ID as on the wire (the method ID of the invocation that opens its pipe).</param>
/// <param name="Kind">NexNet's collection kind.</param>
/// <param name="Mode">Synchronization mode.</param>
/// <param name="State">Whether the collection is synchronized over the session.</param>
/// <param name="Count">Items in the collection.</param>
/// <param name="Version">Version the next change is based on.</param>
/// <param name="PipeIds">Pipes carrying the collection on the session.</param>
public readonly record struct NntlCollectionState(
    ushort Id,
    NexusCollectionKind Kind,
    NexusCollectionMode Mode,
    NexusCollectionState State,
    int Count,
    int Version,
    ushort[] PipeIds);

/// <summary>
/// Encodes NexNet session snapshots into the sections of a snapshot block and decodes them back. Layouts are in
/// docs/internals/nntl-format.md §4.6.1.
/// </summary>
/// <remarks>
/// Each payload is a MessagePack array; later revisions may append elements to any array, so decoders read the
/// elements they know and skip the rest.
/// </remarks>
public static class NntlSnapshotSections
{
    private const int PipeFields = 6;
    private const int InvocationFields = 9;
    private const int LargeMessageFields = 5;
    private const int CollectionFields = 7;

    /// <summary>Builds the three sections (open pipes, pending invocations, collection state) of a session snapshot.</summary>
    /// <param name="snapshot">The snapshot, from <c>INexusSession.GetSnapshot()</c> or a server or client's <c>GetSnapshot()</c>.</param>
    public static NntlSnapshotSection[] Create(NexusSessionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return
        [
            new(NntlSnapshotSectionKind.OpenPipes, EncodeOpenPipes(snapshot.Pipes)),
            new(NntlSnapshotSectionKind.PendingInvocations, EncodeInvocations(snapshot.Invocations, snapshot.LargeMessages)),
            new(NntlSnapshotSectionKind.CollectionState, EncodeCollections(snapshot.Collections)),
        ];
    }

    /// <summary>Encodes an open-pipes section.</summary>
    public static byte[] EncodeOpenPipes(IReadOnlyList<NexusPipeSnapshot> pipes)
    {
        var buffer = new ArrayBufferWriter<byte>();
        var w = new MsgPackWriter(buffer);
        w.WriteArrayHeader(pipes.Count);
        foreach (var p in pipes)
        {
            var flags = (p.IsReady ? NntlPipeStateFlags.Ready : 0)
                        | (p.ReaderCompleted ? NntlPipeStateFlags.ReaderCompleted : 0)
                        | (p.WriterCompleted ? NntlPipeStateFlags.WriterCompleted : 0)
                        | (p.Closing != null ? NntlPipeStateFlags.Closing : 0);
            w.WriteArrayHeader(PipeFields);
            w.Write(p.Id);
            w.Write((byte)flags);
            w.Write(p.Window);
            w.Write(p.ReceiveCredit);
            w.Write(p.Buffered);
            w.Write(p.SendCredit);
        }

        w.Flush();
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>Encodes a pending-invocations section.</summary>
    public static byte[] EncodeInvocations(NexusInvocationSnapshot invocations, IReadOnlyList<NexusLargeMessageSnapshot> largeMessages)
    {
        ArgumentNullException.ThrowIfNull(invocations);
        var buffer = new ArrayBufferWriter<byte>();
        var w = new MsgPackWriter(buffer);
        w.WriteArrayHeader(InvocationFields);
        w.Write(invocations.Queued);
        w.Write(invocations.Executing);
        w.Write(invocations.AwaitingResults);
        w.Write(invocations.InboundCredits);
        w.Write(invocations.OutboundCredits);
        w.Write(invocations.CreditWaits);
        w.Write(invocations.SeparatePoolQueued);
        w.Write(invocations.SeparatePoolExecuting);
        w.WriteArrayHeader(largeMessages.Count);
        foreach (var m in largeMessages)
        {
            w.WriteArrayHeader(LargeMessageFields);
            // NexusDirection and TapDirection agree: inbound 0, outbound 1, both seen from the snapshotting side.
            w.Write((byte)(m.Direction == NexusDirection.Inbound ? TapDirection.Inbound : TapDirection.Outbound));
            w.Write(m.Kind == NexusLargeMessageKind.Result);
            w.Write(m.InvocationId);
            w.Write(m.Length);
            w.Write(m.Transferred);
        }

        w.Flush();
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>Encodes a collection-state section.</summary>
    public static byte[] EncodeCollections(IReadOnlyList<NexusCollectionSnapshot> collections)
    {
        var buffer = new ArrayBufferWriter<byte>();
        var w = new MsgPackWriter(buffer);
        w.WriteArrayHeader(collections.Count);
        foreach (var c in collections)
        {
            w.WriteArrayHeader(CollectionFields);
            w.Write(c.Id);
            w.Write((byte)c.Kind);
            w.Write((byte)c.Mode);
            w.Write((byte)c.State);
            w.Write(c.Count);
            w.Write(c.Version);
            w.WriteArrayHeader(c.PipeIds.Count);
            foreach (var id in c.PipeIds)
                w.Write(id);
        }

        w.Flush();
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>Decodes an open-pipes section.</summary>
    /// <exception cref="NntlFormatException">The payload is malformed.</exception>
    public static NntlPipeState[] DecodeOpenPipes(ReadOnlyMemory<byte> data)
    {
        try
        {
            var r = new MsgPackReader(data);
            var pipes = new NntlPipeState[r.ReadArrayHeader()];
            for (var i = 0; i < pipes.Length; i++)
            {
                var fields = Begin(ref r, PipeFields);
                pipes[i] = new NntlPipeState(r.ReadUInt16(), (NntlPipeStateFlags)r.ReadByte(), r.ReadInt32(), r.ReadInt64(),
                    r.ReadInt64(), r.ReadInt64());
                SkipRest(ref r, fields, PipeFields);
            }

            return pipes;
        }
        catch (NexusSerializationException e)
        {
            throw Malformed(NntlSnapshotSectionKind.OpenPipes, e);
        }
    }

    /// <summary>Decodes a pending-invocations section.</summary>
    /// <exception cref="NntlFormatException">The payload is malformed.</exception>
    public static NntlInvocationState DecodeInvocations(ReadOnlyMemory<byte> data)
    {
        try
        {
            var r = new MsgPackReader(data);
            var fields = Begin(ref r, InvocationFields);
            var queued = r.ReadInt32();
            var executing = r.ReadInt32();
            var awaiting = r.ReadInt32();
            var inbound = r.ReadInt32();
            var outbound = r.ReadInt32();
            var waits = r.ReadInt32();
            var separateQueued = r.ReadInt32();
            var separateExecuting = r.ReadInt32();
            var large = new NntlLargeMessageState[r.ReadArrayHeader()];
            for (var i = 0; i < large.Length; i++)
            {
                var mf = Begin(ref r, LargeMessageFields);
                large[i] = new NntlLargeMessageState((TapDirection)r.ReadByte(), r.ReadBoolean(), r.ReadUInt16(),
                    r.ReadInt64(), r.ReadInt64());
                SkipRest(ref r, mf, LargeMessageFields);
            }

            SkipRest(ref r, fields, InvocationFields);
            return new NntlInvocationState(queued, executing, awaiting, inbound, outbound, waits, separateQueued,
                separateExecuting, large);
        }
        catch (NexusSerializationException e)
        {
            throw Malformed(NntlSnapshotSectionKind.PendingInvocations, e);
        }
    }

    /// <summary>Decodes a collection-state section.</summary>
    /// <exception cref="NntlFormatException">The payload is malformed.</exception>
    public static NntlCollectionState[] DecodeCollections(ReadOnlyMemory<byte> data)
    {
        try
        {
            var r = new MsgPackReader(data);
            var collections = new NntlCollectionState[r.ReadArrayHeader()];
            for (var i = 0; i < collections.Length; i++)
            {
                var fields = Begin(ref r, CollectionFields);
                var id = r.ReadUInt16();
                var kind = (NexusCollectionKind)r.ReadByte();
                var mode = (NexusCollectionMode)r.ReadByte();
                var state = (NexusCollectionState)r.ReadByte();
                var count = r.ReadInt32();
                var version = r.ReadInt32();
                var pipeIds = new ushort[r.ReadArrayHeader()];
                for (var p = 0; p < pipeIds.Length; p++)
                    pipeIds[p] = r.ReadUInt16();
                SkipRest(ref r, fields, CollectionFields);
                collections[i] = new NntlCollectionState(id, kind, mode, state, count, version, pipeIds);
            }

            return collections;
        }
        catch (NexusSerializationException e)
        {
            throw Malformed(NntlSnapshotSectionKind.CollectionState, e);
        }
    }

    private static int Begin(ref MsgPackReader r, int required)
    {
        var count = r.ReadArrayHeader();
        if (count < required)
            throw new NexusSerializationException($"Expected at least {required} elements, found {count}.");
        return count;
    }

    private static void SkipRest(ref MsgPackReader r, int count, int known)
    {
        for (var i = known; i < count; i++)
            r.Skip();
    }

    private static NntlFormatException Malformed(NntlSnapshotSectionKind kind, Exception inner) =>
        new($"Malformed {kind} snapshot section: {inner.Message}", inner);
}
