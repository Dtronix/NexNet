using System.Buffers.Binary;
using NexNet.Inspector.Core.Model;
using NexNet.Messages;

namespace NexNet.Inspector.Core.Framing;

/// <summary>
/// Incremental framer for one direction of a tapped stream. It is fed that direction's transport ops in order (a read's
/// newly arrived bytes, or one flushed write) and emits a <see cref="FrameRecord"/> for each frame as its last byte
/// arrives.
/// </summary>
/// <remarks>
/// <para>
/// Records describe positions only, so the parser never buffers bodies: it carries over at most the 8 bytes of a
/// preamble or a header plus post-header that an op boundary cut, and counts body bytes. Pushing allocates nothing.
/// </para>
/// <para>
/// Malformed input: an unknown type byte, or a preamble with the wrong magic or non-zero reserved bytes, leaves no
/// reliable length to resynchronise on (NexNet itself disconnects with ProtocolError there). The parser emits one
/// <see cref="FrameKind.Malformed"/> record from that byte to the end of its op and then stops framing: later bytes only
/// advance <see cref="StreamOffset"/> and <see cref="UnframedBytes"/>. It never throws for bad bytes, so every record
/// emitted before the fault stands. A preamble's version byte is not checked; the viewer reports it, it does not judge.
/// </para>
/// <para>
/// Gaps: a capture in Drop mode loses whole ops, and an attach starts at whatever op comes next. Either way the next
/// byte need not start a frame, so the parser resynchronises (see <see cref="Gap{TSink}"/> and
/// <see cref="FrameParserStart.Resync"/>): it passes over ops until one holds a run of frame headers that chain to the
/// op's end, and frames again from there.
/// </para>
/// <para>Not thread-safe: one parser per direction, fed from one thread.</para>
/// </remarks>
public sealed class FrameParser
{
    private enum State : byte
    {
        Preamble,
        Header,
        Body,
        Faulted,
        Completed,
        Resync,
    }

    // Evidence a resync candidate needs (see BodyEvidence): at an op's first byte, one frame with a length field that
    // ends exactly with the op (a read of one whole frame is the common case); inside an op, more, since a start found
    // by sliding over arbitrary bytes is likelier to be chance; anywhere, a long chain however the op ends.
    private const int ResyncOpStartEvidence = 1;
    private const int ResyncInsideEvidence = 4;
    private const int ResyncStrongEvidence = 12;

    // Frames framed after a resync that are still on probation: an impossible type byte among them means the resync
    // picked a wrong start, so the parser resynchronises again instead of faulting.
    private const int ResyncProbation = 32;

    // Large enough for the preamble, which is longer than any header plus post-header (6 bytes).
    private readonly byte[] _carry = new byte[FrameLayout.PreambleLength];
    private State _state;
    private long _streamOffset;
    private long _nextSeq;

    // The frame in progress.
    private long _frameOffset;
    private long _frameStartTicks;
    private long _frameFirstOpId;
    private long _lastOpId;
    private long _lastTicks;
    private int _carryCount;
    private int _carryNeeded;
    private byte _headerLength;
    private byte _postHeaderLength;
    private int _bodyLength;
    private int _bodyRemaining;
    private bool _split;

    private long _faultOffset = -1;
    private long _gapBytes;
    private long _skippedBytes;
    private int _probation;

    /// <summary>Creates a parser for one direction.</summary>
    /// <param name="direction">The direction every record is stamped with.</param>
    /// <param name="expectPreamble">
    /// True when this direction's stream starts with the connection preamble: the bytes the client side writes, captured
    /// from the connection's first byte.
    /// </param>
    /// <param name="startOffset">Absolute stream offset of the first byte that will be pushed.</param>
    public FrameParser(TapDirection direction, bool expectPreamble, long startOffset = 0)
        : this(direction, expectPreamble ? FrameParserStart.Preamble : FrameParserStart.FrameBoundary, startOffset)
    {
    }

    /// <summary>Creates a parser for one direction.</summary>
    /// <param name="direction">The direction every record is stamped with.</param>
    /// <param name="start">What the first pushed byte is known to be.</param>
    /// <param name="startOffset">Absolute stream offset of the first byte that will be pushed.</param>
    public FrameParser(TapDirection direction, FrameParserStart start, long startOffset = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(startOffset);
        Direction = direction;
        _streamOffset = startOffset;
        _state = start switch
        {
            FrameParserStart.Preamble => State.Preamble,
            FrameParserStart.Resync => State.Resync,
            _ => State.Header,
        };
        _carryNeeded = start == FrameParserStart.Preamble ? FrameLayout.PreambleLength : 0;
    }

    /// <summary>The direction stamped on every record.</summary>
    public TapDirection Direction { get; }

    /// <summary>Absolute stream offset of the next byte to be pushed.</summary>
    public long StreamOffset => _streamOffset;

    /// <summary>Records emitted so far; also the <see cref="FrameRecord.Seq"/> of the next one.</summary>
    public long FrameCount => _nextSeq;

    /// <summary>True once a malformed range stopped framing.</summary>
    public bool IsFaulted => _faultOffset >= 0;

    /// <summary>Absolute stream offset of the first byte that could not be framed, or -1.</summary>
    public long FaultOffset => _faultOffset;

    /// <summary>Bytes from <see cref="FaultOffset"/> on (all of them unframed); 0 while not faulted.</summary>
    public long UnframedBytes => _faultOffset < 0 ? 0 : _streamOffset - _faultOffset;

    /// <summary>True while the parser looks for a frame start after a gap (or an attach mid-stream).</summary>
    public bool IsResyncing => _state == State.Resync;

    /// <summary>Bytes reported lost through <see cref="Gap{TSink}"/>; they advanced <see cref="StreamOffset"/> unseen.</summary>
    public long GapBytes => _gapBytes;

    /// <summary>Bytes pushed while resynchronising that belong to no record: the tails of frames cut by a gap.</summary>
    public long SkippedBytes => _skippedBytes;

    /// <summary>True while the stream has delivered part of a frame (or preamble) and not yet its end.</summary>
    public bool HasPending => _state == State.Body || (_state is State.Header or State.Preamble && _carryCount > 0);

    /// <summary>Feeds one transport op.</summary>
    /// <param name="opId">The op's id; copied into the records it contributes to.</param>
    /// <param name="ticks">The op's Stopwatch timestamp.</param>
    /// <param name="bytes">The op's bytes: for a read, only the newly arrived suffix.</param>
    /// <param name="sink">Receives every record completed by this op.</param>
    /// <returns>The number of records emitted.</returns>
    /// <exception cref="InvalidOperationException"><see cref="Complete{TSink}"/> was already called.</exception>
    public int Push<TSink>(long opId, long ticks, ReadOnlySpan<byte> bytes, TSink sink)
        where TSink : IFrameSink
    {
        if (_state == State.Completed)
            throw new InvalidOperationException("The parser was completed; no more ops can be pushed.");
        if (bytes.IsEmpty)
            return 0;

        // A frame carried over from an earlier op now spans two ops at least.
        if (HasPending)
            _split = true;
        _lastOpId = opId;
        _lastTicks = ticks;

        var emitted = 0;
        var pos = 0;
        while (pos < bytes.Length)
        {
            switch (_state)
            {
                case State.Faulted:
                    pos = bytes.Length;
                    break;

                case State.Resync:
                {
                    // Only ever entered at an op's first byte: Gap and the constructor are the only ways in.
                    var start = FindFrameStart(bytes.Slice(pos));
                    if (start < 0)
                    {
                        _skippedBytes += bytes.Length - pos;
                        pos = bytes.Length;
                        break;
                    }

                    _skippedBytes += start;
                    pos += start;
                    _carryCount = 0;
                    _carryNeeded = 0;
                    _probation = ResyncProbation;
                    _state = State.Header;
                    break;
                }

                case State.Preamble:
                {
                    if (_carryCount == 0)
                        BeginFrame(opId, ticks, pos);
                    pos += Carry(bytes.Slice(pos));
                    if (_carryCount < _carryNeeded)
                        break;

                    var preamble = _carry.AsSpan(0, FrameLayout.PreambleLength);
                    if (!preamble.StartsWith(FrameLayout.PreambleMagic) || preamble[4] != 0 || preamble[5] != 0 || preamble[6] != 0)
                    {
                        emitted += Fault(sink, MessageType.Unset, bytes.Length);
                        pos = bytes.Length;
                        break;
                    }

                    _headerLength = FrameLayout.PreambleLength;
                    Emit(sink, FrameKind.Preamble);
                    emitted++;
                    break;
                }

                case State.Header:
                {
                    if (_carryCount == 0)
                    {
                        BeginFrame(opId, ticks, pos);
                        var type = bytes[pos];
                        if (!FrameLayout.TryGetLayout(type, out _headerLength, out _postHeaderLength))
                        {
                            if (_probation > 0)
                            {
                                // The resync started at a wrong offset: look again from the next op.
                                _skippedBytes += bytes.Length - pos;
                                _probation = 0;
                                _state = State.Resync;
                                pos = bytes.Length;
                                break;
                            }

                            emitted += Fault(sink, (MessageType)type, bytes.Length);
                            pos = bytes.Length;
                            break;
                        }

                        _carryNeeded = _headerLength + _postHeaderLength;
                    }

                    pos += Carry(bytes.Slice(pos));
                    if (_carryCount < _carryNeeded)
                        break;

                    if (_headerLength == 1)
                    {
                        Emit(sink, FrameKind.Frame);
                        emitted++;
                        break;
                    }

                    _bodyLength = BinaryPrimitives.ReadUInt16LittleEndian(_carry.AsSpan(1));
                    if (_bodyLength == 0)
                    {
                        Emit(sink, FrameKind.Frame);
                        emitted++;
                        break;
                    }

                    _bodyRemaining = _bodyLength;
                    _state = State.Body;
                    break;
                }

                case State.Body:
                {
                    var take = Math.Min(_bodyRemaining, bytes.Length - pos);
                    _bodyRemaining -= take;
                    pos += take;
                    if (_bodyRemaining == 0)
                    {
                        Emit(sink, FrameKind.Frame);
                        emitted++;
                    }

                    break;
                }
            }
        }

        _streamOffset += bytes.Length;
        return emitted;
    }

    /// <summary>
    /// Reports ops of this direction that the capture lost (a Drop-mode gap). The frame in progress, if any, is emitted
    /// flagged <see cref="FrameFlags.Incomplete"/>: its tail was among the lost bytes. The lost bytes advance
    /// <see cref="StreamOffset"/>, and the parser resynchronises on the ops that follow.
    /// </summary>
    /// <param name="droppedBytes">Bytes the lost ops carried.</param>
    /// <param name="sink">Receives the incomplete record.</param>
    /// <returns>The number of records emitted (0 or 1).</returns>
    /// <exception cref="InvalidOperationException"><see cref="Complete{TSink}"/> was already called.</exception>
    /// <remarks>A faulted parser stays faulted: a gap explains missing bytes, not malformed ones.</remarks>
    public int Gap<TSink>(long droppedBytes, TSink sink)
        where TSink : IFrameSink
    {
        if (_state == State.Completed)
            throw new InvalidOperationException("The parser was completed; no more ops can be pushed.");
        ArgumentOutOfRangeException.ThrowIfNegative(droppedBytes);

        var emitted = 0;
        if (HasPending)
        {
            sink.Add(BuildPending());
            _nextSeq++;
            emitted = 1;
        }

        _carryCount = 0;
        _carryNeeded = 0;
        _split = false;
        _streamOffset += droppedBytes;
        _gapBytes += droppedBytes;
        if (_state != State.Faulted)
            _state = State.Resync;
        return emitted;
    }

    /// <summary>Reports lost ops, appending the incomplete record, if any, to a list.</summary>
    /// <inheritdoc cref="Gap{TSink}(long, TSink)"/>
    public int Gap(long droppedBytes, List<FrameRecord> sink) => Gap(droppedBytes, new ListSink(sink));

    /// <summary>Feeds one transport op, appending the completed records to a list.</summary>
    /// <inheritdoc cref="Push{TSink}(long, long, ReadOnlySpan{byte}, TSink)"/>
    public int Push(long opId, long ticks, ReadOnlySpan<byte> bytes, List<FrameRecord> sink)
        => Push(opId, ticks, bytes, new ListSink(sink));

    /// <summary>
    /// Describes the frame in progress, flagged <see cref="FrameFlags.Incomplete"/>, without consuming it: for a live
    /// view that shows a frame while its tail is still on the way. Its <see cref="FrameRecord.Seq"/> is the one the
    /// frame gets when it completes.
    /// </summary>
    /// <param name="frame">The partial frame; fields whose bytes have not arrived are 0.</param>
    /// <returns>False when no frame is in progress.</returns>
    public bool TryGetPending(out FrameRecord frame)
    {
        if (!HasPending)
        {
            frame = default;
            return false;
        }

        frame = BuildPending();
        return true;
    }

    /// <summary>
    /// Ends the stream: emits the frame in progress, if any, flagged <see cref="FrameFlags.Incomplete"/>. Later pushes
    /// throw.
    /// </summary>
    /// <param name="sink">Receives the incomplete record.</param>
    /// <returns>True when an incomplete record was emitted.</returns>
    public bool Complete<TSink>(TSink sink)
        where TSink : IFrameSink
    {
        var pending = HasPending;
        if (pending)
        {
            sink.Add(BuildPending());
            _nextSeq++;
        }

        _state = State.Completed;
        return pending;
    }

    /// <summary>Ends the stream, appending the incomplete record, if any, to a list.</summary>
    /// <inheritdoc cref="Complete{TSink}(TSink)"/>
    public bool Complete(List<FrameRecord> sink) => Complete(new ListSink(sink));

    // The first offset in an op where frames plausibly start: a chain of headers that ends exactly with the op and
    // carries enough evidence, or a long chain. Each whole frame adds evidence by how much of it could be checked. An op
    // written by the tapped endpoint is one flush of whole frames, so its first byte is accepted on one plausible
    // header; a read can start anywhere.
    private int FindFrameStart(ReadOnlySpan<byte> op)
    {
        for (var p = 0; p < op.Length; p++)
        {
            if (!TryWalkFrames(op.Slice(p), out var evidence, out var exact))
                continue;

            var accept = evidence >= ResyncStrongEvidence
                         || (exact && evidence >= (p == 0 ? ResyncOpStartEvidence : ResyncInsideEvidence))
                         || (p == 0 && Direction == TapDirection.Outbound);
            if (accept)
                return p;
        }

        return -1;
    }

    // Walks frame headers from the start of 'bytes'. False when a header is impossible; otherwise the evidence the
    // whole frames gave and whether the last one ends exactly at the end of 'bytes'.
    private static bool TryWalkFrames(ReadOnlySpan<byte> bytes, out int evidence, out bool exact)
    {
        evidence = 0;
        exact = false;
        var pos = 0;
        while (pos < bytes.Length)
        {
            var type = bytes[pos];
            if (!FrameLayout.TryGetLayout(type, out var headerLength, out var postHeaderLength))
                return false;

            if (headerLength == 1)
            {
                // A disconnect is the last thing a stream carries. A Ping is a single common byte value: no evidence.
                if (FrameLayout.IsDisconnect(type) && pos + 1 != bytes.Length)
                    return false;
                pos++;
            }
            else
            {
                if (pos + 3 > bytes.Length)
                    return true;

                var bodyLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(pos + 1));
                var body = pos + 3 + postHeaderLength;
                var weight = BodyEvidence((MessageType)type, bytes, body, bodyLength);
                if (weight < 0)
                    return false;

                var end = body + bodyLength;
                if (end > bytes.Length)
                    return true;
                pos = end;
                evidence += weight;
            }

            if (evidence >= ResyncStrongEvidence)
                return true;
        }

        exact = true;
        return true;
    }

    // What the bytes at hand say about a body: -1 impossible, 1 nothing contradicts it, 2 its envelope's MessagePack
    // array header is exactly the one its type writes (spec §4).
    private static int BodyEvidence(MessageType type, ReadOnlySpan<byte> bytes, int body, int bodyLength)
    {
        int expected;
        switch (type)
        {
            case MessageType.MessageChunk:
                return body - 1 >= bytes.Length || bytes[body - 1] <= 1 ? 1 : -1;
            case MessageType.PipeWindowUpdate:
                return bodyLength == 4 ? 1 : -1;
            case MessageType.DuplexPipeWrite:
                return 1;
            case MessageType.ClientGreeting:
            case MessageType.ClientGreetingReconnection:
                expected = 0x96;
                break;
            case MessageType.Invocation:
            case MessageType.MessageStart:
                expected = 0x94;
                break;
            case MessageType.InvocationCancellation:
                expected = 0x91;
                break;
            case MessageType.MessageAbort:
            case MessageType.DuplexPipeUpdateState:
                expected = 0x93;
                break;
            case MessageType.InvocationResult:
                // Two elements, or three with a value.
                if (bodyLength == 0)
                    return -1;
                if (body >= bytes.Length)
                    return 1;
                return bytes[body] is 0x92 or 0x93 ? 2 : -1;
            case MessageType.ServerGreeting:
                if (bodyLength == 0)
                    return -1;
                if (body >= bytes.Length)
                    return 1;
                return bytes[body] is >= 0x94 and <= 0x9F ? 2 : -1;
            default:
                if (bodyLength == 0)
                    return -1;
                return body >= bytes.Length || (bytes[body] & 0xF0) == 0x90 ? 1 : -1;
        }

        if (bodyLength == 0)
            return -1;
        if (body >= bytes.Length)
            return 1;
        return bytes[body] == expected ? 2 : -1;
    }

    private void BeginFrame(long opId, long ticks, int pos)
    {
        _frameOffset = _streamOffset + pos;
        _frameStartTicks = ticks;
        _frameFirstOpId = opId;
        _split = false;
        _headerLength = 0;
        _postHeaderLength = 0;
        _bodyLength = 0;
        _bodyRemaining = 0;
    }

    private int Carry(ReadOnlySpan<byte> bytes)
    {
        var take = Math.Min(_carryNeeded - _carryCount, bytes.Length);
        bytes.Slice(0, take).CopyTo(_carry.AsSpan(_carryCount));
        _carryCount += take;
        return take;
    }

    private void Emit<TSink>(TSink sink, FrameKind kind)
        where TSink : IFrameSink
    {
        var frame = Build(kind, _headerLength + _postHeaderLength + _bodyLength, headerComplete: true);
        sink.Add(frame);
        _nextSeq++;
        if (_probation > 0)
            _probation--;
        _carryCount = 0;
        _carryNeeded = 0;
        _state = State.Header;
    }

    // The malformed range runs from the frame start to the end of the current op (opLength bytes, starting at _streamOffset).
    private int Fault<TSink>(TSink sink, MessageType type, int opLength)
        where TSink : IFrameSink
    {
        _faultOffset = _frameOffset;
        var length = checked((int)(_streamOffset + opLength - _frameOffset));
        sink.Add(new FrameRecord
        {
            Seq = _nextSeq++,
            Direction = Direction,
            Kind = FrameKind.Malformed,
            Type = type,
            Flags = _split ? FrameFlags.TransportSplit : FrameFlags.None,
            StartTicks = _frameStartTicks,
            EndTicks = _lastTicks,
            FirstOpId = _frameFirstOpId,
            LastOpId = _lastOpId,
            Offset = _frameOffset,
            Length = length,
        });
        _carryCount = 0;
        _state = State.Faulted;
        return 1;
    }

    private FrameRecord BuildPending()
    {
        var frame = Build(
            _state == State.Preamble ? FrameKind.Preamble : FrameKind.Frame,
            checked((int)(_streamOffset - _frameOffset)),
            headerComplete: _state == State.Body);
        return frame with { Flags = frame.Flags | FrameFlags.Incomplete };
    }

    private FrameRecord Build(FrameKind kind, int length, bool headerComplete)
    {
        var type = MessageType.Unset;
        var headerLength = _headerLength;
        if (kind == FrameKind.Preamble)
            headerLength = FrameLayout.PreambleLength;
        else if (_carryCount > 0)
            type = (MessageType)_carry[0];

        var target = FrameTarget.None;
        ushort targetId = 0;
        byte chunkKind = 0;
        if (headerComplete && kind == FrameKind.Frame)
        {
            switch (type)
            {
                case MessageType.DuplexPipeWrite:
                case MessageType.PipeWindowUpdate:
                    target = FrameTarget.Pipe;
                    targetId = (ushort)(_carry[3] | (_carry[4] << 8));
                    break;
                case MessageType.MessageChunk:
                    target = FrameTarget.Invocation;
                    targetId = BinaryPrimitives.ReadUInt16LittleEndian(_carry.AsSpan(3));
                    chunkKind = _carry[5];
                    break;
            }
        }

        // The body length is known as soon as the length field arrived, even if the post-header has not.
        var bodyLength = _bodyLength;
        if (!headerComplete && headerLength == 3 && _carryCount >= 3)
            bodyLength = BinaryPrimitives.ReadUInt16LittleEndian(_carry.AsSpan(1));

        return new FrameRecord
        {
            Seq = _nextSeq,
            Direction = Direction,
            Kind = kind,
            Type = type,
            Flags = _split ? FrameFlags.TransportSplit : FrameFlags.None,
            StartTicks = _frameStartTicks,
            EndTicks = _lastTicks,
            FirstOpId = _frameFirstOpId,
            LastOpId = _lastOpId,
            Offset = _frameOffset,
            Length = length,
            HeaderLength = headerLength,
            PostHeaderLength = _postHeaderLength,
            BodyLength = bodyLength,
            Target = target,
            TargetId = targetId,
            ChunkKind = chunkKind,
        };
    }

    private readonly struct ListSink(List<FrameRecord> list) : IFrameSink
    {
        public void Add(in FrameRecord frame) => list.Add(frame);
    }
}
