using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using NexNet.Collections;
using NexNet.Inspector.Core.Format;
using NexNet.Inspector.Core.Framing;
using NexNet.Inspector.Core.Model;
using NexNet.Serialization;

namespace NexNet.Inspector.Core.Assembly;

public sealed partial class MessageAssembler
{
    // NexusPipeWriter sends a write in chunks of at most this many bytes; a full chunk means the write went on.
    private const int PipeChunkLength = ushort.MaxValue;

    // Bounds the DuplexPipe invocations kept while waiting for their Ready: without a schema, pipe IDs guessed from
    // their arguments that never open (plain integer arguments) would otherwise accumulate.
    private const int MaxPendingOpeners = 256;

    private readonly List<PipeRow> _pipes = [];
    private readonly List<PipeTracker> _pipeTrackers = [];
    private readonly List<PipeEventRow> _pipeEvents = [];
    private readonly List<ChannelItemRow> _items = [];
    private readonly List<CollectionRow> _collections = [];
    private readonly List<CollectionMessageRow> _collectionMessages = [];
    private readonly List<PipeOpener> _pendingOpeners = [];

    // Pipe ID -> open (or closing) pipe; a closed ID can be reused by a new pipe.
    private readonly Dictionary<ushort, int> _openPipes = [];

    // Pipe ID -> latest pipe that had it, for late frames after the close.
    private readonly Dictionary<ushort, int> _lastPipeById = [];

    /// <summary>Pipes, in the order they were first seen.</summary>
    public ReadOnlySpan<PipeRow> Pipes => CollectionsMarshal.AsSpan(_pipes);

    /// <summary>State changes and credit grants of every pipe, in arrival order.</summary>
    public ReadOnlySpan<PipeEventRow> PipeEvents => CollectionsMarshal.AsSpan(_pipeEvents);

    /// <summary>Items of channel and collection pipes.</summary>
    public ReadOnlySpan<ChannelItemRow> ChannelItems => CollectionsMarshal.AsSpan(_items);

    /// <summary>Collections.</summary>
    public ReadOnlySpan<CollectionRow> Collections => CollectionsMarshal.AsSpan(_collections);

    /// <summary>Collection messages.</summary>
    public ReadOnlySpan<CollectionMessageRow> CollectionMessages => CollectionsMarshal.AsSpan(_collectionMessages);

    /// <summary>The data runs of a pipe in one direction, as indices into <see cref="Messages"/>, in stream order.</summary>
    /// <param name="pipe">Index into <see cref="Pipes"/>.</param>
    /// <param name="direction">The direction.</param>
    public ReadOnlySpan<int> GetPipeRuns(int pipe, TapDirection direction)
        => CollectionsMarshal.AsSpan(_pipeTrackers[pipe].Runs[(int)direction]);

    /// <summary>
    /// Says what a pipe carries. Switching a pipe to <see cref="PipeContent.Channel"/> or
    /// <see cref="PipeContent.Collection"/> splits its data into items, first replaying the data already seen.
    /// </summary>
    /// <param name="pipe">Index into <see cref="Pipes"/>.</param>
    /// <param name="content">What the pipe carries.</param>
    /// <param name="collectionId">For a collection, its ID; by default the opener's method ID.</param>
    /// <remarks>
    /// Items are only split from a known boundary: the pipe's first byte, so a pipe whose open was not captured is
    /// marked <see cref="PipeRow.ChannelUnavailable"/> instead.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The pipe was already given another non-raw content.</exception>
    public void SetPipeContent(int pipe, PipeContent content, ushort? collectionId = null)
    {
        var row = _pipes[pipe];
        if (row.Content == content || content == PipeContent.Raw)
            return;
        if (row.Content != PipeContent.Raw)
            throw new InvalidOperationException($"Pipe {pipe} already carries {row.Content}.");

        var collection = -1;
        if (content == PipeContent.Collection)
        {
            collection = _collections.Count;
            var id = collectionId
                     ?? (row.OpenerInvocation >= 0 ? _invocations[row.OpenerInvocation].MethodId : (ushort)0);
            _collections.Add(new CollectionRow { Index = collection, CollectionId = id, Pipe = pipe, LastVersion = -1 });
        }

        var unavailable = row.OpenSeq < 0 || row.Lifecycle == PipeLifecycle.Refused;
        CollectionsMarshal.AsSpan(_pipes)[pipe] = row with
        {
            Content = content,
            Collection = collection,
            ChannelUnavailable = unavailable,
        };
        Version++;
        if (unavailable)
            return;

        var tracker = _pipeTrackers[pipe];
        for (var d = 0; d < 2; d++)
        {
            var direction = (TapDirection)d;
            tracker.Splitters[d] = new ChannelSplitter { Active = true, Item = -1, Remaining = -1 };
            var runs = tracker.Runs[d];
            for (var r = 0; r < runs.Count; r++)
            {
                var run = _messages[runs[r]];
                for (var f = run.FirstFragment; f >= 0; f = _fragments[f].Next)
                {
                    var fragment = _fragments[f];
                    FeedChannel(pipe, direction, fragment.Seq, fragment.StreamOffset, run.PipeOffset + fragment.MessageOffset,
                        fragment.Length);
                }
            }
        }
    }

    private void OnPipeWrite(in FrameRecord frame)
    {
        var pipe = FindOrAddPipe(frame.TargetId, createIfClosed: true);
        var tracker = _pipeTrackers[pipe];
        var d = (int)frame.Direction;
        var pipeOffset = tracker.Bytes[d];

        var run = tracker.CurrentRun[d];
        if (run >= 0 && tracker.LastFull[d])
        {
            var count = AppendToMessage(run, frame, frame.BodyOffset, frame.BodyLength);
            if (count == 2)
                AddFrameFlags(frame.Direction, _messages[run].FirstSeq, FrameFlags.NexNetSplit);
            AddFrameFlags(frame.Direction, frame.Seq, FrameFlags.NexNetSplit);
        }
        else
        {
            run = NewMessage(new AssembledMessage
            {
                Kind = AssembledMessageKind.PipeRun,
                Direction = frame.Direction,
                State = AssemblyState.Assembling,
                InnerType = frame.Type,
                TotalLength = -1,
                FirstSeq = frame.Seq,
                LastSeq = frame.Seq,
                Invocation = -1,
                Pipe = pipe,
                PipeOffset = pipeOffset,
                AbortReason = MessageAbortReason.None,
            });
            AppendToMessage(run, frame, frame.BodyOffset, frame.BodyLength);
            tracker.Runs[d].Add(run);
            tracker.CurrentRun[d] = run;
        }

        tracker.LastFull[d] = frame.BodyLength == PipeChunkLength;
        SetMessageState(run, tracker.LastFull[d] ? AssemblyState.Assembling : AssemblyState.Complete);
        tracker.Bytes[d] += frame.BodyLength;

        ref var row = ref CollectionsMarshal.AsSpan(_pipes)[pipe];
        row = frame.Direction == TapDirection.Inbound
            ? row with { InboundBytes = tracker.Bytes[d], InboundRuns = tracker.Runs[d].Count }
            : row with { OutboundBytes = tracker.Bytes[d], OutboundRuns = tracker.Runs[d].Count };
        SetLink(frame.Direction, frame.Seq, EntityKind.Pipe, pipe, run);

        FeedChannel(pipe, frame.Direction, frame.Seq, frame.BodyOffset, pipeOffset, frame.BodyLength);
    }

    private void OnWindowUpdate(in FrameRecord frame)
    {
        Span<byte> credit = stackalloc byte[4];
        if (frame.BodyLength != 4)
        {
            AddLinkFlags(frame.Direction, frame.Seq, FrameLinkFlags.BodyMalformed);
            return;
        }

        if (!Store(frame.Direction).TryCopyTo(frame.BodyOffset, credit))
        {
            AddLinkFlags(frame.Direction, frame.Seq, FrameLinkFlags.BytesUnavailable);
            return;
        }

        var window = BinaryPrimitives.ReadUInt32LittleEndian(credit);
        var pipe = FindOrAddPipe(frame.TargetId, createIfClosed: false);
        if (pipe < 0)
            return;

        _pipeEvents.Add(new PipeEventRow(pipe, frame.Direction, frame.Seq, PipeEventKind.WindowUpdate, PipeStateFlags.None, window));
        AddGrant(pipe, frame.Direction, window);
        SetLink(frame.Direction, frame.Seq, EntityKind.Pipe, pipe, -1);
    }

    private void OnPipeState(in FrameRecord frame)
    {
        if (!TryReadBody(frame, out var body))
            return;

        var reader = new MsgPackReader(body);
        reader.ReadArrayHeader(3);
        var pipeId = reader.ReadUInt16();
        var state = (PipeStateFlags)reader.ReadByte();
        var window = reader.ReadUInt32();

        const PipeStateFlags completeBits =
            PipeStateFlags.ClientWriterServerReaderComplete | PipeStateFlags.ClientReaderServerWriterComplete;
        int pipe;
        PipeEventKind kind;
        if ((state & PipeStateFlags.Refused) != 0)
        {
            // Refused carries the opener's partial ID: only its own byte is set (§7.5).
            kind = PipeEventKind.Refused;
            pipe = NewPipe(pipeId, PipeLifecycle.Refused, frame.Seq, Opposite(frame.Direction));
            var openerByte = (byte)pipeId != 0 ? (byte)pipeId : (byte)(pipeId >> 8);
            var opener = MatchOpener(Opposite(frame.Direction), openerByte, openerByte);
            if (opener >= 0)
            {
                LinkOpener(pipe, opener);
                ref var inv = ref CollectionsMarshal.AsSpan(_invocations)[opener];
                inv = inv with { State = InvocationState.Refused };
            }
        }
        else if ((state & completeBits) == 0 && (state & PipeStateFlags.Ready) != 0)
        {
            // Ready comes from the registering side; the opener is the other.
            kind = PipeEventKind.Ready;
            var openerDirection = Opposite(frame.Direction);
            if (_openPipes.TryGetValue(pipeId, out pipe) && _pipes[pipe].OpenSeq < 0)
            {
                ref var existing = ref CollectionsMarshal.AsSpan(_pipes)[pipe];
                existing = existing with { OpenSeq = frame.Seq, OpenerDirection = openerDirection };
            }
            else
            {
                pipe = NewPipe(pipeId, PipeLifecycle.Open, frame.Seq, openerDirection);
                _openPipes[pipeId] = pipe;
            }

            AddGrant(pipe, frame.Direction, window);
            var opener = MatchOpener(openerDirection, (byte)pipeId, (byte)(pipeId >> 8));
            if (opener >= 0)
                LinkOpener(pipe, opener);
        }
        else
        {
            kind = PipeEventKind.State;
            pipe = FindOrAddPipe(pipeId, createIfClosed: false);
            if (pipe >= 0)
                ApplyCompletion(pipe, frame.Direction, state);
        }

        if (pipe < 0)
            return;

        _pipeEvents.Add(new PipeEventRow(pipe, frame.Direction, frame.Seq, kind, state, window));
        ref var row = ref CollectionsMarshal.AsSpan(_pipes)[pipe];
        row = frame.Direction == TapDirection.Inbound ? row with { InboundState = state } : row with { OutboundState = state };
        SetLink(frame.Direction, frame.Seq, EntityKind.Pipe, pipe, -1);
    }

    private void ApplyCompletion(int pipe, TapDirection sender, PipeStateFlags state)
    {
        const PipeStateFlags completeBits =
            PipeStateFlags.ClientWriterServerReaderComplete | PipeStateFlags.ClientReaderServerWriterComplete;
        var tracker = _pipeTrackers[pipe];
        tracker.Sent[(int)sender] |= state;
        ref var row = ref CollectionsMarshal.AsSpan(_pipes)[pipe];
        if ((tracker.Sent[0] & completeBits) == completeBits && (tracker.Sent[1] & completeBits) == completeBits)
        {
            // Both sides sent Complete: the ID is released (§7.3), so later frames with it belong to a new pipe.
            row = row with { Lifecycle = PipeLifecycle.Closed };
            if (_openPipes.TryGetValue(row.PipeId, out var open) && open == pipe)
                _openPipes.Remove(row.PipeId);
            for (var d = 0; d < 2; d++)
            {
                tracker.CurrentRun[d] = -1;
                ref var splitter = ref tracker.Splitters[d];
                if (splitter.Item >= 0)
                {
                    SetItemState(splitter.Item, AssemblyState.Truncated);
                    splitter.Item = -1;
                }
            }
        }
        else if (row.Lifecycle == PipeLifecycle.Open)
        {
            row = row with { Lifecycle = PipeLifecycle.Closing };
        }
    }

    private void AddGrant(int pipe, TapDirection sender, uint window)
    {
        ref var row = ref CollectionsMarshal.AsSpan(_pipes)[pipe];
        // A grant sent by the tapped endpoint lets its peer write to it.
        row = sender == TapDirection.Outbound
            ? row with { OutboundGrants = row.OutboundGrants + window }
            : row with { InboundGrants = row.InboundGrants + window };
    }

    private int FindOrAddPipe(ushort pipeId, bool createIfClosed)
    {
        if (_openPipes.TryGetValue(pipeId, out var pipe))
            return pipe;

        // A late state or window frame after the close belongs to the last pipe with that ID. Anything else for an ID
        // that is not open means the pipe opened before the capture started.
        if (!createIfClosed && _lastPipeById.TryGetValue(pipeId, out pipe))
            return pipe;

        pipe = NewPipe(pipeId, PipeLifecycle.Open, -1, null);
        _openPipes[pipeId] = pipe;
        return pipe;
    }

    private int NewPipe(ushort pipeId, PipeLifecycle lifecycle, long openSeq, TapDirection? openerDirection)
    {
        var index = _pipes.Count;
        _pipes.Add(new PipeRow
        {
            Index = index,
            PipeId = pipeId,
            OpenerDirection = openerDirection,
            OpenerInvocation = -1,
            Lifecycle = lifecycle,
            OpenSeq = openSeq,
            Collection = -1,
        });
        _pipeTrackers.Add(new PipeTracker());
        _lastPipeById[pipeId] = index;
        return index;
    }

    private void LinkOpener(int pipe, int invocation)
    {
        ref var row = ref CollectionsMarshal.AsSpan(_pipes)[pipe];
        row = row with { OpenerInvocation = invocation };
        ref var inv = ref CollectionsMarshal.AsSpan(_invocations)[invocation];
        if (inv.Pipe < 0)
            inv = inv with { Pipe = pipe };

        if (PipeContentResolver is { } resolver && row.Lifecycle != PipeLifecycle.Refused)
        {
            var content = resolver.Resolve(_invocations[invocation]);
            if (content != PipeContent.Raw)
                SetPipeContent(pipe, content);
        }
    }

    // Records the pipe IDs a DuplexPipe invocation sent. With a resolver that knows the method (the schema), that is
    // exactly its pipe argument; otherwise every top-level argument that is an integer 1-255 is a candidate.
    private void RegisterPipeOpener(int invocation, TapDirection caller, ReadOnlySequence<byte> arguments)
    {
        var opener = new PipeOpener { Invocation = invocation, Direction = caller };
        var exact = -1;
        if (PipeContentResolver is { } resolver && resolver.TryGetPipeArgument(_invocations[invocation], out exact) && exact < 0)
            return;

        try
        {
            var reader = new MsgPackReader(arguments);
            var count = reader.ReadArrayHeader();
            for (var i = 0; i < count; i++)
            {
                var code = reader.PeekCode();
                if ((exact < 0 || i == exact) && (code is >= 1 and <= 0x7F || code == MsgPackCode.UInt8))
                    opener.Add(reader.ReadByte());
                else
                    reader.Skip();
                if (i == exact)
                    break;
            }
        }
        catch (NexusSerializationException)
        {
            // Arguments the decoder cannot walk still gave whatever IDs preceded the fault.
        }

        if (opener.IsEmpty)
            return;
        if (_pendingOpeners.Count >= MaxPendingOpeners)
            _pendingOpeners.RemoveAt(0);
        _pendingOpeners.Add(opener);
    }

    // Finds the oldest DuplexPipe invocation from the opener's side that sent the opener's local ID.
    private int MatchOpener(TapDirection openerDirection, byte clientByte, byte serverByte)
    {
        int first;
        int second;
        if (Role is { } role)
        {
            // The opener sent its own local byte: the client's byte when the opener is the client.
            var openerIsClient = (openerDirection == TapDirection.Outbound) == (role == NntlSessionRole.Client);
            first = openerIsClient ? clientByte : serverByte;
            second = -1;
        }
        else
        {
            first = clientByte;
            second = serverByte;
        }

        var openers = CollectionsMarshal.AsSpan(_pendingOpeners);
        for (var pass = 0; pass < 2; pass++)
        {
            var id = pass == 0 ? first : second;
            if (id <= 0)
                continue;

            for (var i = 0; i < openers.Length; i++)
            {
                if (openers[i].Direction != openerDirection || !openers[i].Contains((byte)id))
                    continue;

                var invocation = openers[i].Invocation;
                openers[i].Remove((byte)id);
                if (openers[i].IsEmpty)
                    _pendingOpeners.RemoveAt(i);
                return invocation;
            }
        }

        return -1;
    }

    // Splits a run of pipe bytes into channel items: a LEB128 length prefix, then that many value bytes (§7.4).
    private void FeedChannel(int pipe, TapDirection direction, long seq, long streamOffset, long pipeOffset, int length)
    {
        var tracker = _pipeTrackers[pipe];
        ref var splitter = ref tracker.Splitters[(int)direction];
        if (!splitter.Active || splitter.Faulted || length == 0)
            return;

        var store = Store(direction);
        var position = 0;
        var portionStart = 0;
        var segmentStart = 0;
        ReadOnlyMemory<byte> segment = default;
        while (position < length)
        {
            if (position - segmentStart >= segment.Length)
            {
                if (!store.TryGetSegment(streamOffset + position, out segment) || segment.IsEmpty)
                {
                    FaultChannel(pipe, ref splitter, AssemblyState.Truncated);
                    return;
                }

                segmentStart = position;
            }

            if (splitter.Item < 0)
            {
                splitter.Item = NewItem(pipe, direction, pipeOffset + position, seq);
                splitter.Prefix = 0;
                splitter.PrefixBytes = 0;
                splitter.Remaining = -1;
                portionStart = position;
            }

            var span = segment.Span.Slice(position - segmentStart);
            if (span.Length > length - position)
                span = span.Slice(0, length - position);

            if (splitter.Remaining < 0)
            {
                var b = span[0];
                position++;
                splitter.Prefix |= (ulong)(b & 0x7F) << (7 * splitter.PrefixBytes);
                splitter.PrefixBytes++;
                if ((b & 0x80) != 0)
                {
                    if (splitter.PrefixBytes == 5)
                    {
                        AppendToItem(splitter.Item, direction, seq, streamOffset + portionStart, position - portionStart);
                        FaultChannel(pipe, ref splitter, AssemblyState.Malformed);
                        return;
                    }

                    continue;
                }

                if (splitter.Prefix > int.MaxValue)
                {
                    AppendToItem(splitter.Item, direction, seq, streamOffset + portionStart, position - portionStart);
                    FaultChannel(pipe, ref splitter, AssemblyState.Malformed);
                    return;
                }

                splitter.Remaining = (long)splitter.Prefix;
                ref var item = ref CollectionsMarshal.AsSpan(_items)[splitter.Item];
                item = item with { PrefixLength = splitter.PrefixBytes, ValueLength = (int)splitter.Prefix };
            }
            else
            {
                var take = (int)Math.Min(splitter.Remaining, span.Length);
                position += take;
                splitter.Remaining -= take;
            }

            if (splitter.Remaining == 0)
            {
                var item = splitter.Item;
                AppendToItem(item, direction, seq, streamOffset + portionStart, position - portionStart);
                splitter.Item = -1;
                CompleteItem(item);
            }
        }

        if (splitter.Item >= 0 && position > portionStart)
            AppendToItem(splitter.Item, direction, seq, streamOffset + portionStart, position - portionStart);
    }

    private void FaultChannel(int pipe, ref ChannelSplitter splitter, AssemblyState itemState)
    {
        splitter.Faulted = true;
        if (splitter.Item >= 0)
            SetItemState(splitter.Item, itemState);
        splitter.Item = -1;
        ref var row = ref CollectionsMarshal.AsSpan(_pipes)[pipe];
        row = row with { ChannelUnavailable = true };
    }

    private int NewItem(int pipe, TapDirection direction, long pipeOffset, long seq)
    {
        var index = _items.Count;
        _items.Add(new ChannelItemRow
        {
            Index = index,
            Pipe = pipe,
            Direction = direction,
            State = AssemblyState.Assembling,
            PipeOffset = pipeOffset,
            ValueLength = -1,
            FirstSeq = seq,
            LastSeq = seq,
            CollectionMessage = -1,
            FirstFragment = -1,
            LastFragment = -1,
        });
        return index;
    }

    private void AppendToItem(int item, TapDirection direction, long seq, long streamOffset, int length)
    {
        ref var row = ref CollectionsMarshal.AsSpan(_items)[item];
        var fragment = AppendFragment(row.LastFragment, direction, seq, row.Length, streamOffset, length);
        row = row with
        {
            FirstFragment = row.FirstFragment < 0 ? fragment : row.FirstFragment,
            LastFragment = fragment,
            FragmentCount = row.FragmentCount + 1,
            Length = row.Length + length,
            LastSeq = seq,
        };

        // An item that crosses frames is a NexNet split; the first frame learns it only now.
        if (row.FragmentCount == 2)
            AddFrameFlags(direction, row.FirstSeq, FrameFlags.NexNetSplit);
        if (row.FragmentCount >= 2)
            AddFrameFlags(direction, seq, FrameFlags.NexNetSplit);
    }

    private void SetItemState(int item, AssemblyState state)
    {
        ref var row = ref CollectionsMarshal.AsSpan(_items)[item];
        row = row with { State = state };
    }

    private void CompleteItem(int item)
    {
        SetItemState(item, AssemblyState.Complete);
        var row = _items[item];
        var pipe = _pipes[row.Pipe];
        if (pipe.Content == PipeContent.Collection && pipe.Collection >= 0)
            DecodeCollectionMessage(pipe.Collection, row);
    }

    // Reads the envelope of a collection message (§4.11): [tag, [flags, ...]]; values stay raw.
    private void DecodeCollectionMessage(int collection, in ChannelItemRow item)
    {
        var message = new CollectionMessageRow
        {
            Index = _collectionMessages.Count,
            Collection = collection,
            Item = item.Index,
            Direction = item.Direction,
            Kind = CollectionMessageKind.Unknown,
            Version = -1,
            TotalValues = -1,
            Position = -1,
            ToPosition = -1,
            Value = PayloadRange.None,
        };

        var peek = Math.Min(PeekLength, item.ValueLength);
        if (TryPeek(item.Ref, item.PrefixLength, peek, out var envelope))
        {
            try
            {
                message = ReadCollectionEnvelope(envelope, item, message);
            }
            catch (NexusSerializationException)
            {
                message = message with { Malformed = true };
            }
        }
        else
        {
            message = message with { Malformed = true };
        }

        _collectionMessages.Add(message);
        ref var itemRow = ref CollectionsMarshal.AsSpan(_items)[item.Index];
        itemRow = itemRow with { CollectionMessage = message.Index };
        ref var row = ref CollectionsMarshal.AsSpan(_collections)[collection];
        row = row with
        {
            MessageCount = row.MessageCount + 1,
            LastVersion = message.Version >= 0 ? message.Version : row.LastVersion,
        };
    }

    private static CollectionMessageRow ReadCollectionEnvelope(
        ReadOnlyMemory<byte> envelope,
        in ChannelItemRow item,
        CollectionMessageRow message)
    {
        var reader = new MsgPackReader(envelope);
        reader.ReadArrayHeader(2);
        var tag = reader.ReadUInt16();
        reader.ReadArrayHeader();
        var flags = (NexusCollectionMessageFlags)reader.ReadByte();
        var kind = tag <= (ushort)CollectionMessageKind.Noop ? (CollectionMessageKind)tag : CollectionMessageKind.Unknown;
        message = message with { Kind = kind, Flags = flags };

        switch (kind)
        {
            case CollectionMessageKind.ResetStart:
                message = message with { Version = reader.ReadInt32() };
                return message with { TotalValues = reader.ReadInt32() };
            case CollectionMessageKind.ResetValues:
                return message with { Value = ValueToEnd(item, reader.Consumed) };
            case CollectionMessageKind.Clear:
                return message with { Version = reader.ReadInt32() };
            case CollectionMessageKind.Insert:
            case CollectionMessageKind.Replace:
                message = message with { Version = reader.ReadInt32() };
                message = message with { Position = reader.ReadInt32() };
                return message with { Value = ValueToEnd(item, reader.Consumed) };
            case CollectionMessageKind.Move:
                message = message with { Version = reader.ReadInt32() };
                message = message with { Position = reader.ReadInt32() };
                return message with { ToPosition = reader.ReadInt32() };
            case CollectionMessageKind.Remove:
                message = message with { Version = reader.ReadInt32() };
                return message with { Position = reader.ReadInt32() };
            default:
                return message;
        }
    }

    // The last element of a collection message body runs to the end of the item.
    private static PayloadRange ValueToEnd(in ChannelItemRow item, long consumed)
        => new(item.Ref, item.PrefixLength + consumed, item.ValueLength - consumed);

    private sealed class PipeTracker
    {
        public readonly List<int>[] Runs = [[], []];
        public readonly int[] CurrentRun = [-1, -1];
        public readonly bool[] LastFull = new bool[2];
        public readonly long[] Bytes = new long[2];
        public readonly PipeStateFlags[] Sent = new PipeStateFlags[2];
        public readonly ChannelSplitter[] Splitters =
        [
            new ChannelSplitter { Item = -1, Remaining = -1 },
            new ChannelSplitter { Item = -1, Remaining = -1 },
        ];
    }

    private struct ChannelSplitter
    {
        public bool Active;
        public bool Faulted;
        public int Item;
        public ulong Prefix;
        public int PrefixBytes;
        public long Remaining;
    }

    // Candidate local pipe IDs of one DuplexPipe invocation, as a 256-bit set.
    private struct PipeOpener
    {
        public int Invocation;
        public TapDirection Direction;
        private UInt128 _low;
        private UInt128 _high;

        public readonly bool IsEmpty => _low == UInt128.Zero && _high == UInt128.Zero;

        public void Add(byte id)
        {
            if (id < 128)
                _low |= UInt128.One << id;
            else
                _high |= UInt128.One << (id - 128);
        }

        public void Remove(byte id)
        {
            if (id < 128)
                _low &= ~(UInt128.One << id);
            else
                _high &= ~(UInt128.One << (id - 128));
        }

        public readonly bool Contains(byte id)
            => id < 128 ? (_low & (UInt128.One << id)) != UInt128.Zero : (_high & (UInt128.One << (id - 128))) != UInt128.Zero;
    }
}
