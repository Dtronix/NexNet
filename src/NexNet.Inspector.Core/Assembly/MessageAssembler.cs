using System.Buffers;
using System.Runtime.InteropServices;
using NexNet.Inspector.Core.Format;
using NexNet.Inspector.Core.Framing;
using NexNet.Inspector.Core.Model;
using NexNet.Messages;
using NexNet.Serialization;

namespace NexNet.Inspector.Core.Assembly;

/// <summary>
/// Turns the frame records of both directions of one session into messages and entities: large messages reassembled
/// from MessageStart / MessageChunk / MessageAbort, pipe data runs, channel items, collection messages, invocations,
/// pipes and greetings. It adds the <see cref="FrameFlags.NexNetSplit"/> (N) and <see cref="FrameFlags.Cancel"/> (X)
/// flags to the frames it keeps, and answers trace queries: the fragments of a message and the frames interleaved
/// between them.
/// </summary>
/// <remarks>
/// <para>
/// Feed it every record of both directions in capture order, as the parsers emit them (it is an
/// <see cref="IFrameSink"/>), after the record's bytes are in that direction's <see cref="IStreamBytes"/>. Only
/// envelope fields are read (IDs, method, flags, states, lengths); argument, result and item values are recorded as
/// <see cref="PayloadRange"/>s for the schema decoder.
/// </para>
/// <para>
/// Rows are value types in lists, updated in place as later frames arrive; the spans the accessors return are valid
/// until the next <see cref="Add"/>. Per frame the assembler allocates nothing beyond list growth and a row for each
/// new entity. Bad bodies never throw: the frame's <see cref="FrameLink.Flags"/> say what went wrong.
/// </para>
/// <para>Not thread-safe.</para>
/// </remarks>
public sealed partial class MessageAssembler : IFrameSink
{
    // Longest envelope read from the front of an assembled message or a channel item: an Invocation needs at most 9
    // bytes, an InvocationResult 6 and a collection message 22.
    private const int PeekLength = 32;

    private readonly IStreamBytes _inbound;
    private readonly IStreamBytes _outbound;
    private readonly List<FrameRecord> _inboundFrames = [];
    private readonly List<FrameRecord> _outboundFrames = [];
    private readonly List<FrameLink> _inboundLinks = [];
    private readonly List<FrameLink> _outboundLinks = [];
    private readonly long[] _seqBase = [0, 0];

    private readonly List<Fragment> _fragments = [];
    private readonly List<AssembledMessage> _messages = [];
    private readonly List<InvocationRow> _invocations = [];
    private readonly List<GreetingRow> _greetings = [];

    // (caller direction, invocation ID) -> invocation awaiting a result.
    private readonly Dictionary<int, int> _pendingInvocations = [];

    // (sender direction, kind, invocation ID) -> large message still open.
    private readonly Dictionary<int, int> _activeStreams = [];

    // Bodies that an op boundary split are copied here; no body is longer than a frame.
    private readonly byte[] _scratch = new byte[ushort.MaxValue];
    private readonly byte[] _peek = new byte[PeekLength];

    /// <summary>Creates an assembler for one session.</summary>
    /// <param name="inbound">Bytes the tapped endpoint read.</param>
    /// <param name="outbound">Bytes the tapped endpoint wrote.</param>
    /// <param name="role">
    /// Which side the tapped endpoint is, when known. Otherwise it is inferred from the greetings; it is needed to tell
    /// the opener's byte of a pipe ID and, with a schema, which nexus interface an invocation calls.
    /// </param>
    public MessageAssembler(IStreamBytes inbound, IStreamBytes outbound, NntlSessionRole? role = null)
    {
        ArgumentNullException.ThrowIfNull(inbound);
        ArgumentNullException.ThrowIfNull(outbound);
        _inbound = inbound;
        _outbound = outbound;
        Role = role;
    }

    /// <summary>Which side the tapped endpoint is; null until given or inferred.</summary>
    public NntlSessionRole? Role { get; private set; }

    /// <summary>Optional: decides what new pipes carry (see <see cref="IPipeContentResolver"/>).</summary>
    public IPipeContentResolver? PipeContentResolver { get; set; }

    /// <summary>Increments on every change, including flags added to frames already kept.</summary>
    public long Version { get; private set; }

    /// <summary>Large messages and pipe runs.</summary>
    public ReadOnlySpan<AssembledMessage> Messages => CollectionsMarshal.AsSpan(_messages);

    /// <summary>Invocations, in the order their first frame arrived.</summary>
    public ReadOnlySpan<InvocationRow> Invocations => CollectionsMarshal.AsSpan(_invocations);

    /// <summary>Greetings.</summary>
    public ReadOnlySpan<GreetingRow> Greetings => CollectionsMarshal.AsSpan(_greetings);

    /// <summary>The kept frames of one direction, with assembly's flags, indexed by sequence number minus the first's.</summary>
    /// <param name="direction">The direction.</param>
    public ReadOnlySpan<FrameRecord> GetFrames(TapDirection direction) => CollectionsMarshal.AsSpan(Frames(direction));

    /// <summary>Gets a kept frame by sequence number.</summary>
    /// <param name="direction">The direction.</param>
    /// <param name="seq">The frame's sequence number.</param>
    /// <param name="frame">The frame, with assembly's flags.</param>
    /// <returns>False when no such frame was added.</returns>
    public bool TryGetFrame(TapDirection direction, long seq, out FrameRecord frame)
    {
        var index = IndexOf(direction, seq);
        if (index < 0)
        {
            frame = default;
            return false;
        }

        frame = Frames(direction)[index];
        return true;
    }

    /// <summary>What assembly made of a frame.</summary>
    /// <param name="direction">The direction.</param>
    /// <param name="seq">The frame's sequence number.</param>
    /// <returns>The link; <see cref="FrameLink.None"/> for an unknown frame.</returns>
    public FrameLink GetLink(TapDirection direction, long seq)
    {
        var index = IndexOf(direction, seq);
        return index < 0 ? FrameLink.None : Links(direction)[index];
    }

    /// <summary>The logical message a frame carries (part of).</summary>
    /// <param name="direction">The direction.</param>
    /// <param name="seq">The frame's sequence number.</param>
    /// <returns>
    /// The assembled message the frame is part of, else the frame as a message of its own; None for an unknown frame.
    /// A data frame of a channel pipe names its run; its items are in <see cref="ChannelItems"/>.
    /// </returns>
    public MessageRef GetMessage(TapDirection direction, long seq)
    {
        var index = IndexOf(direction, seq);
        if (index < 0)
            return MessageRef.None;

        var link = Links(direction)[index];
        return link.Message >= 0 ? _messages[link.Message].Ref : MessageRef.ForFrame(direction, seq);
    }

    /// <summary>Adds the next record of one direction.</summary>
    /// <param name="frame">The record; its bytes must already be in that direction's stream.</param>
    /// <exception cref="ArgumentException">The record does not follow the previous one of its direction.</exception>
    public void Add(in FrameRecord frame)
    {
        var direction = frame.Direction;
        var frames = Frames(direction);
        if (frames.Count == 0)
            _seqBase[(int)direction] = frame.Seq;
        else if (frame.Seq != _seqBase[(int)direction] + frames.Count)
            throw new ArgumentException("Records of a direction must be added in sequence order, without gaps.", nameof(frame));

        frames.Add(frame);
        Links(direction).Add(FrameLink.None);
        Version++;

        // Both sides write a preamble (spec §1), so it says nothing about the role; the greetings do.
        if (frame.Kind == FrameKind.Preamble)
            return;

        if (frame.Kind != FrameKind.Frame || (frame.Flags & FrameFlags.Incomplete) != 0)
            return;

        try
        {
            Dispatch(frame);
        }
        catch (NexusSerializationException)
        {
            AddLinkFlags(direction, frame.Seq, FrameLinkFlags.BodyMalformed);
        }
    }

    /// <summary>
    /// Ends the session: large messages and channel items still assembling become <see cref="AssemblyState.Truncated"/>.
    /// Invocations without a result stay pending.
    /// </summary>
    public void Complete()
    {
        var messages = CollectionsMarshal.AsSpan(_messages);
        for (var i = 0; i < messages.Length; i++)
        {
            if (messages[i].State != AssemblyState.Assembling)
                continue;

            messages[i] = messages[i] with
            {
                State = messages[i].Kind == AssembledMessageKind.PipeRun ? AssemblyState.Complete : AssemblyState.Truncated,
            };
        }

        var items = CollectionsMarshal.AsSpan(_items);
        for (var i = 0; i < items.Length; i++)
        {
            if (items[i].State == AssemblyState.Assembling)
                items[i] = items[i] with { State = AssemblyState.Truncated };
        }

        _activeStreams.Clear();
        Version++;
    }

    /// <summary>
    /// Reports that the capture lost ops of one direction (a Drop-mode gap). What was in flight in that direction can
    /// no longer complete: its large messages become <see cref="AssemblyState.Truncated"/> (chunks after the gap start
    /// orphan messages), its pipe runs end there, and channel items stop being split on its pipes, since no item
    /// boundary is known after lost bytes. Call it when the gap is reached in capture order, after the parser of that
    /// direction was told (<see cref="FrameParser.Gap{TSink}"/>), so the frame the gap cut is already added.
    /// </summary>
    /// <param name="direction">The direction that lost ops.</param>
    public void Gap(TapDirection direction)
    {
        // Removing while enumerating is allowed for Dictionary since .NET Core 3.0.
        foreach (var (key, message) in _activeStreams)
        {
            if ((TapDirection)(key >> 17) != direction)
                continue;

            _activeStreams.Remove(key);
            if (_messages[message].State is AssemblyState.Assembling or AssemblyState.Orphan)
                SetMessageState(message, AssemblyState.Truncated);
        }

        var d = (int)direction;
        for (var pipe = 0; pipe < _pipeTrackers.Count; pipe++)
        {
            var tracker = _pipeTrackers[pipe];
            var run = tracker.CurrentRun[d];
            if (run >= 0 && _messages[run].State == AssemblyState.Assembling)
                SetMessageState(run, AssemblyState.Truncated);
            tracker.CurrentRun[d] = -1;
            tracker.LastFull[d] = false;

            ref var splitter = ref tracker.Splitters[d];
            if (splitter.Active && !splitter.Faulted)
                FaultChannel(pipe, ref splitter, AssemblyState.Truncated);
        }

        GapCount++;
        Version++;
    }

    /// <summary>Gaps reported through <see cref="Gap"/>.</summary>
    public int GapCount { get; private set; }

    /// <summary>
    /// Seeds the assembler from the snapshot taken when a capture attached to a running session, so frames of what was
    /// already open resolve from the first one: open pipes are known (and a collection's pipes carry it), and a large
    /// message that was in transfer collects its remaining chunks at their true offsets, linked to its invocation.
    /// </summary>
    /// <param name="seed">The decoded snapshot.</param>
    /// <exception cref="InvalidOperationException">A frame was already added.</exception>
    /// <remarks>
    /// The snapshot counts pending invocations but does not list their IDs, so results of ordinary pending calls still
    /// arrive as orphans. A pipe seeded this way has no known item boundary, so it is
    /// <see cref="PipeRow.ChannelUnavailable"/> when it carries a channel or collection.
    /// </remarks>
    public void Seed(SnapshotSeed seed)
    {
        ArgumentNullException.ThrowIfNull(seed);
        if (_inboundFrames.Count + _outboundFrames.Count > 0)
            throw new InvalidOperationException("A snapshot seeds the assembler before its first frame.");

        foreach (var state in seed.Pipes)
        {
            // Without Ready only this side's half of the ID is set; the peer's Ready frame will open it.
            if ((state.Flags & NntlPipeStateFlags.Ready) == 0 || _openPipes.ContainsKey(state.Id))
                continue;

            var lifecycle = (state.Flags & NntlPipeStateFlags.Closing) != 0 ? PipeLifecycle.Closing : PipeLifecycle.Open;
            var pipe = NewPipe(state.Id, lifecycle, -1, null);
            CollectionsMarshal.AsSpan(_pipes)[pipe] = _pipes[pipe] with { FromSnapshot = true };
            _openPipes[state.Id] = pipe;
        }

        foreach (var collection in seed.Collections)
        {
            foreach (var pipeId in collection.PipeIds)
            {
                if (!_openPipes.TryGetValue(pipeId, out var pipe) || _pipes[pipe].Content != PipeContent.Raw)
                    continue;

                SetPipeContent(pipe, PipeContent.Collection, collection.Id);
                ref var row = ref CollectionsMarshal.AsSpan(_collections)[_pipes[pipe].Collection];
                row = row with { LastVersion = collection.Version };
            }
        }

        if (seed.Invocations is { } invocations)
        {
            foreach (var large in invocations.LargeMessages)
                SeedLargeMessage(large);
        }

        Version++;
    }

    private void SeedLargeMessage(in NntlLargeMessageState large)
    {
        // The snapshot sees from the tapped endpoint, as frame directions do: Inbound is a stream it receives.
        var kind = large.IsResult ? LargeMessageKind.Result : LargeMessageKind.Argument;
        var key = StreamKey(large.Direction, kind, large.InvocationId);
        if (_activeStreams.ContainsKey(key))
            return;

        // Orphan with a known total: chunks land at their true offsets, and the bytes before the capture are a hole
        // TryGetPayload refuses to read across.
        var message = NewMessage(new AssembledMessage
        {
            Kind = AssembledMessageKind.Large,
            Direction = large.Direction,
            State = AssemblyState.Orphan,
            LargeKind = kind,
            InvocationId = large.InvocationId,
            InnerType = kind == LargeMessageKind.Argument ? MessageType.Invocation : MessageType.InvocationResult,
            TotalLength = large.Length,
            Length = large.Transferred,
            FirstSeq = -1,
            LastSeq = -1,
            Invocation = -1,
            Pipe = -1,
            AbortReason = MessageAbortReason.None,
        });
        _activeStreams[key] = message;
        var messageRef = _messages[message].Ref;

        int invocation;
        if (kind == LargeMessageKind.Argument)
        {
            invocation = NewInvocation(large.Direction, large.InvocationId, messageRef, keyed: true);
        }
        else
        {
            invocation = FindOrAddForResult(Opposite(large.Direction), large.InvocationId, out _);
            ref var row = ref CollectionsMarshal.AsSpan(_invocations)[invocation];
            row = row with { Result = messageRef };
        }

        CollectionsMarshal.AsSpan(_messages)[message] = _messages[message] with { Invocation = invocation };
    }

    /// <summary>Gets bytes of a message: a <see cref="PayloadRange"/> from a row.</summary>
    /// <param name="range">The range.</param>
    /// <param name="bytes">The bytes, over the stream's buffers; one allocation per extra buffer they span.</param>
    /// <returns>False for a None range, or when bytes are missing from the stream or the message.</returns>
    public bool TryGetPayload(in PayloadRange range, out ReadOnlySequence<byte> bytes)
    {
        bytes = ReadOnlySequence<byte>.Empty;
        if (range.IsNone || range.Offset < 0)
            return false;

        var message = range.Message;
        if (message.Kind == MessageRefKind.Frame)
        {
            if (!TryGetFrame(message.Direction, message.Seq, out var frame) || range.Offset + range.Length > frame.BodyLength)
                return false;
            return Store(message.Direction).TryGetSequence(frame.BodyOffset + range.Offset, range.Length, out bytes);
        }

        if (!TryGetChain(message, out var first, out var length) || range.Offset + range.Length > length)
            return false;

        var builder = new SequenceBuilder();
        var end = range.Offset + range.Length;
        var cursor = range.Offset;
        for (var f = first; f >= 0; f = _fragments[f].Next)
        {
            var fragment = _fragments[f];
            var fragmentEnd = fragment.MessageOffset + fragment.Length;
            if (fragmentEnd <= range.Offset || fragment.Length == 0)
                continue;
            if (fragment.MessageOffset >= end)
                break;

            // A message seeded mid-transfer has no fragments for the bytes sent before the capture.
            var from = Math.Max(range.Offset, fragment.MessageOffset);
            if (from > cursor)
                return false;
            var to = Math.Min(end, fragmentEnd);
            if (!builder.Append(Store(fragment.Direction), fragment.StreamOffset + (from - fragment.MessageOffset), to - from))
                return false;
            cursor = to;
        }

        if (cursor < end)
            return false;

        bytes = builder.Build();
        return true;
    }

    private void Dispatch(in FrameRecord frame)
    {
        switch (frame.Type)
        {
            case MessageType.ClientGreeting:
            case MessageType.ClientGreetingReconnection:
                OnClientGreeting(frame);
                break;
            case MessageType.ServerGreeting:
                OnServerGreeting(frame);
                break;
            case MessageType.Invocation:
                OnInvocation(frame);
                break;
            case MessageType.InvocationResult:
                OnInvocationResult(frame);
                break;
            case MessageType.InvocationCancellation:
                OnInvocationCancellation(frame);
                break;
            case MessageType.MessageStart:
                OnMessageStart(frame);
                break;
            case MessageType.MessageChunk:
                OnMessageChunk(frame);
                break;
            case MessageType.MessageAbort:
                OnMessageAbort(frame);
                break;
            case MessageType.DuplexPipeWrite:
                OnPipeWrite(frame);
                break;
            case MessageType.PipeWindowUpdate:
                OnWindowUpdate(frame);
                break;
            case MessageType.DuplexPipeUpdateState:
                OnPipeState(frame);
                break;
        }
    }

    private void OnClientGreeting(in FrameRecord frame)
    {
        if (!TryReadBody(frame, out var body))
            return;

        var reader = new MsgPackReader(body);
        reader.ReadArrayHeader(6);
        var version = reader.ReadString();
        var serverHash = reader.ReadInt32();
        var clientHash = reader.ReadInt32();
        var token = ReadTokenRange(ref reader, MessageRef.ForFrame(frame.Direction, frame.Seq));
        var maxMessageSize = reader.ReadInt32();
        var credits = reader.ReadInt32();

        Role ??= frame.Direction == TapDirection.Outbound ? NntlSessionRole.Client : NntlSessionRole.Server;
        AddGreeting(frame, new GreetingRow
        {
            Kind = frame.Type == MessageType.ClientGreeting ? GreetingKind.Client : GreetingKind.ClientReconnection,
            Version = version,
            ServerNexusHash = serverHash,
            ClientNexusHash = clientHash,
            Token = token,
            MaxMessageSize = maxMessageSize,
            InvocationCredits = credits,
        });
    }

    private void OnServerGreeting(in FrameRecord frame)
    {
        if (!TryReadBody(frame, out var body))
            return;

        var reader = new MsgPackReader(body);
        var count = reader.ReadArrayHeader();
        if (count < 4)
        {
            AddLinkFlags(frame.Direction, frame.Seq, FrameLinkFlags.BodyMalformed);
            return;
        }

        var expectedClientHash = reader.ReadInt32();
        var clientId = reader.ReadInt64();
        var maxMessageSize = reader.ReadInt32();
        var credits = reader.ReadInt32();
        var idleTimeout = count > 4 ? reader.ReadUInt32() : 0;

        Role ??= frame.Direction == TapDirection.Outbound ? NntlSessionRole.Server : NntlSessionRole.Client;
        AddGreeting(frame, new GreetingRow
        {
            Kind = GreetingKind.Server,
            ClientNexusHash = expectedClientHash,
            ClientId = clientId,
            MaxMessageSize = maxMessageSize,
            InvocationCredits = credits,
            IdleTimeoutMs = idleTimeout,
            Token = PayloadRange.None,
        });
    }

    private void AddGreeting(in FrameRecord frame, GreetingRow row)
    {
        var index = _greetings.Count;
        _greetings.Add(row with { Index = index, Direction = frame.Direction, Seq = frame.Seq });
        SetLink(frame.Direction, frame.Seq, EntityKind.Greeting, index, -1);
    }

    private static PayloadRange ReadTokenRange(ref MsgPackReader reader, MessageRef message)
    {
        // ReadBinaryHeader returns -1 for nil, which spec §5.4 reads as no token.
        var length = reader.ReadBinaryHeader();
        if (length < 0)
            return PayloadRange.None;

        var offset = reader.Consumed;
        reader.ReadRawBytes(length);
        return new PayloadRange(message, offset, length);
    }

    /// <summary>Finds the authentication token in a ClientGreeting or ClientGreetingReconnection body.</summary>
    /// <param name="body">The frame body (after the 3-byte header).</param>
    /// <param name="offset">Offset of the token's first byte in the body.</param>
    /// <param name="length">The token's length; -1 when it is nil.</param>
    /// <returns>False when the body does not decode that far.</returns>
    public static bool TryFindGreetingToken(ReadOnlyMemory<byte> body, out int offset, out int length)
    {
        offset = 0;
        length = -1;
        try
        {
            var reader = new MsgPackReader(body);
            reader.ReadArrayHeader(6);
            reader.Skip();
            reader.ReadInt32();
            reader.ReadInt32();
            length = reader.ReadBinaryHeader();
            offset = (int)reader.Consumed;
            return length < 0 || offset + length <= body.Length;
        }
        catch (NexusSerializationException)
        {
            return false;
        }
    }

    private bool TryReadBody(in FrameRecord frame, out ReadOnlyMemory<byte> body)
    {
        var store = Store(frame.Direction);
        if (store.TryGetSegment(frame.BodyOffset, out var segment) && segment.Length >= frame.BodyLength)
        {
            body = segment.Slice(0, frame.BodyLength);
            return true;
        }

        var copy = _scratch.AsMemory(0, frame.BodyLength);
        if (store.TryCopyTo(frame.BodyOffset, copy.Span))
        {
            body = copy;
            return true;
        }

        body = default;
        AddLinkFlags(frame.Direction, frame.Seq, FrameLinkFlags.BytesUnavailable);
        return false;
    }

    // Copies the first bytes of a chained message into _peek.
    private bool TryPeek(MessageRef message, long offset, int length, out ReadOnlyMemory<byte> bytes)
    {
        bytes = default;
        if (!TryGetPayload(new PayloadRange(message, offset, length), out var sequence))
            return false;

        sequence.CopyTo(_peek);
        bytes = _peek.AsMemory(0, length);
        return true;
    }

    private bool TryGetChain(MessageRef message, out int first, out long length)
    {
        switch (message.Kind)
        {
            case MessageRefKind.Assembled when (uint)message.Index < (uint)_messages.Count:
                first = _messages[message.Index].FirstFragment;
                length = _messages[message.Index].Length;
                return true;
            case MessageRefKind.ChannelItem when (uint)message.Index < (uint)_items.Count:
                first = _items[message.Index].FirstFragment;
                length = _items[message.Index].Length;
                return true;
            default:
                first = -1;
                length = 0;
                return false;
        }
    }

    // Appends a fragment to a chain whose last fragment is 'last' (-1 for an empty chain); returns its index.
    private int AppendFragment(int last, TapDirection direction, long seq, long messageOffset, long streamOffset, int length)
    {
        var index = _fragments.Count;
        _fragments.Add(new Fragment
        {
            Direction = direction,
            Seq = seq,
            MessageOffset = messageOffset,
            StreamOffset = streamOffset,
            Length = length,
            Next = -1,
        });
        if (last >= 0)
            CollectionsMarshal.AsSpan(_fragments)[last].Next = index;
        return index;
    }

    // Adds a fragment to an assembled message, returning its new fragment count.
    private int AppendToMessage(int message, in FrameRecord frame, long streamOffset, int length)
    {
        ref var row = ref CollectionsMarshal.AsSpan(_messages)[message];
        var fragment = AppendFragment(row.LastFragment, frame.Direction, frame.Seq, row.Length, streamOffset, length);
        row = row with
        {
            FirstFragment = row.FirstFragment < 0 ? fragment : row.FirstFragment,
            LastFragment = fragment,
            FragmentCount = row.FragmentCount + 1,
            Length = row.Length + length,
            LastSeq = frame.Direction == row.Direction ? frame.Seq : row.LastSeq,
        };
        return row.FragmentCount;
    }

    private int NewMessage(AssembledMessage row)
    {
        var index = _messages.Count;
        _messages.Add(row with { Index = index, FirstFragment = -1, LastFragment = -1 });
        return index;
    }

    // Adds flags to every frame that carries part of a message.
    private void AddMessageFlags(MessageRef message, FrameFlags flags)
    {
        if (message.Kind == MessageRefKind.Frame)
        {
            AddFrameFlags(message.Direction, message.Seq, flags);
            return;
        }

        if (!TryGetChain(message, out var first, out _))
            return;

        for (var f = first; f >= 0; f = _fragments[f].Next)
            AddFrameFlags(_fragments[f].Direction, _fragments[f].Seq, flags);
    }

    private void AddFrameFlags(TapDirection direction, long seq, FrameFlags flags)
    {
        var index = IndexOf(direction, seq);
        if (index < 0)
            return;

        ref var frame = ref CollectionsMarshal.AsSpan(Frames(direction))[index];
        if ((frame.Flags & flags) == flags)
            return;

        frame = frame with { Flags = frame.Flags | flags };
        Version++;
    }

    private void SetLink(TapDirection direction, long seq, EntityKind entity, int entityIndex, int message)
    {
        var index = IndexOf(direction, seq);
        if (index < 0)
            return;

        ref var link = ref CollectionsMarshal.AsSpan(Links(direction))[index];
        link = link with { Entity = entity, EntityIndex = entityIndex, Message = message };
    }

    private void AddLinkFlags(TapDirection direction, long seq, FrameLinkFlags flags)
    {
        var index = IndexOf(direction, seq);
        if (index < 0)
            return;

        ref var link = ref CollectionsMarshal.AsSpan(Links(direction))[index];
        link = link with { Flags = link.Flags | flags };
    }

    private int IndexOf(TapDirection direction, long seq)
    {
        var index = seq - _seqBase[(int)direction];
        return index >= 0 && index < Frames(direction).Count ? (int)index : -1;
    }

    private List<FrameRecord> Frames(TapDirection direction)
        => direction == TapDirection.Inbound ? _inboundFrames : _outboundFrames;

    private List<FrameLink> Links(TapDirection direction)
        => direction == TapDirection.Inbound ? _inboundLinks : _outboundLinks;

    private IStreamBytes Store(TapDirection direction)
        => direction == TapDirection.Inbound ? _inbound : _outbound;

    private static TapDirection Opposite(TapDirection direction)
        => direction == TapDirection.Inbound ? TapDirection.Outbound : TapDirection.Inbound;
}
