using NexNet.Inspector.App.Model;
using NexNet.Inspector.Core.Assembly;
using NexNet.Inspector.Core.Framing;
using NexNet.Inspector.Core.Model;
using NexNet.Inspector.Core.Schema;
using NexNet.Inspector.Core.Session;

namespace NexNet.Inspector.App.Controls.Hex;

/// <summary>What one stream byte belongs to.</summary>
/// <param name="Frame">The frame that covers it.</param>
/// <param name="Role">Its role in that frame.</param>
/// <param name="Field">
/// The bytes of the smallest field around it that the hex view can name: the decoded field (its part in this frame),
/// else the header field (type, length, post-header), else the whole preamble, ping or body.
/// </param>
/// <param name="Hover">The decoded field, for the property tree; <see cref="FieldHover.None"/> when the byte is no decoded field's.</param>
public readonly record struct HexFieldHit(FrameKey Frame, HexCellRole Role, HexRange Field, FieldHover Hover);

/// <summary>
/// Links hex bytes and decoded fields both ways: a byte under the pointer resolves to its frame, its header field or the
/// decoded field that covers it (<see cref="DecodedValue.FindField"/> on the message's arguments or result), and a
/// hovered or selected field (from the property tree) maps back to stream ranges through the message's fragments
/// (<see cref="MessageAssembler.GetFragments"/>), so a field split across chunks lights up in every chunk.
/// </summary>
/// <remarks>
/// The last decoded value is cached (keyed by message and assembler version), so moving the pointer across one message
/// decodes it once. Call under the session's gate; UI thread.
/// </remarks>
public sealed class HexLinker
{
    private readonly List<TraceFragment> _fragments = [];
    private CaptureSession? _cachedSession;
    private MessageRef _cachedMessage = MessageRef.None;
    private long _cachedVersion = -1;
    private DecodedValue? _cachedValue;

    /// <summary>Resolves the byte at <paramref name="offset"/> of <paramref name="direction"/>.</summary>
    /// <returns>False when no frame covers it.</returns>
    public bool TryResolve(CaptureSession session, TapDirection direction, long offset, out HexFieldHit hit)
    {
        ArgumentNullException.ThrowIfNull(session);
        hit = default;
        ReadOnlySpan<FrameRecord> frames = session.Assembler.GetFrames(direction);
        int index = HexLayout.FrameAt(frames, offset);
        if (index < 0)
            return false;

        FrameRecord frame = frames[index];
        FrameKey key = new(direction, frame.Seq);
        HexCellRole role = HexRowBuilder.RoleOf(frame, offset - frame.Offset);
        long frameEnd = frame.Offset + frame.Length;
        HexRange field = role switch
        {
            HexCellRole.Preamble or HexCellRole.OtherFrame => new HexRange(direction, frame.Offset, frameEnd),
            HexCellRole.Type => new HexRange(direction, frame.Offset, frame.Offset + 1),
            HexCellRole.Length => new HexRange(direction, frame.Offset + 1, frame.Offset + frame.HeaderLength),
            HexCellRole.PostHeader => new HexRange(direction, frame.Offset + frame.HeaderLength, frame.BodyOffset),
            _ => new HexRange(direction, frame.BodyOffset, frameEnd),
        };

        FieldHover hover = FieldHover.None;
        if (role == HexCellRole.Body && TryFindField(session, frame, offset, out hover))
        {
            // The field's part in this frame: a field can run on into the next chunk, which the hover ring still shows.
            long start = Math.Max(frame.BodyOffset, offset - PayloadOffset(session, frame, offset) + hover.Offset);
            long end = Math.Min(frameEnd, start + hover.Length);
            if (end > start)
                field = new HexRange(direction, start, end);
        }

        hit = new HexFieldHit(key, role, field, hover);
        return true;
    }

    /// <summary>Adds the stream ranges that carry part of a message's payload.</summary>
    /// <param name="session">The session.</param>
    /// <param name="message">The message.</param>
    /// <param name="offset">Offset in the message's payload coordinates.</param>
    /// <param name="length">Bytes.</param>
    /// <param name="destination">Receives the ranges in stream order.</param>
    public void AddStreamRanges(CaptureSession session, in MessageRef message, long offset, long length, List<HexRange> destination)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(destination);
        if (message.IsNone || length <= 0)
            return;

        _fragments.Clear();
        session.Assembler.GetFragments(message, _fragments);
        long end = offset + length;
        foreach (TraceFragment fragment in _fragments)
        {
            long from = Math.Max(offset, fragment.MessageOffset);
            long to = Math.Min(end, fragment.MessageOffset + fragment.Length);
            if (to > from)
            {
                long at = fragment.StreamOffset + (from - fragment.MessageOffset);
                destination.Add(new HexRange(fragment.Direction, at, at + (to - from)));
            }
        }
    }

    /// <summary>
    /// Fills <paramref name="highlight"/> from the selection and the hovered field: the selected frames, the ringed
    /// selected bytes (a byte range, or the type byte of a selected frame or message) and the ringed hovered bytes
    /// (the decoded field's ranges, else <paramref name="localHover"/>, the header field under the pointer).
    /// </summary>
    public void Build(CaptureSession? session, in Selection selection, in FieldHover hover, HexRange? localHover, HexHighlight highlight)
    {
        ArgumentNullException.ThrowIfNull(highlight);
        highlight.Clear();
        if (session is null)
            return;

        MessageAssembler assembler = session.Assembler;
        switch (selection.Kind)
        {
            case SelectionKind.Frame:
                AddFrame(assembler, selection.Frame, highlight, ringType: true);
                break;
            case SelectionKind.Message:
                AddMessageFrames(assembler, selection.Message, highlight);
                break;
            case SelectionKind.Entity when selection.Entity == EntityKind.Invocation
                                           && (uint)selection.EntityIndex < (uint)assembler.Invocations.Length:
                AddMessageFrames(assembler, assembler.Invocations[selection.EntityIndex].Request, highlight);
                break;
            case SelectionKind.ByteRange:
                if (selection.Frame.Seq >= 0)
                    AddFrame(assembler, selection.Frame, highlight, ringType: false);
                highlight.Selected.Add(new HexRange(selection.Direction, selection.Bytes.Offset, selection.Bytes.End));
                break;
        }

        if (!hover.IsNone)
            AddStreamRanges(session, hover.Message, hover.Offset, hover.Length, highlight.Hovered);
        else if (localHover is { } local)
            highlight.Hovered.Add(local);
    }

    private void AddMessageFrames(MessageAssembler assembler, in MessageRef message, HexHighlight highlight)
    {
        if (message.IsNone)
            return;

        _fragments.Clear();
        assembler.GetFragments(message, _fragments);
        bool ringed = false;
        foreach (TraceFragment fragment in _fragments)
        {
            FrameKey key = new(fragment.Direction, fragment.Seq);
            if (highlight.IsSelected(key.Direction, key.Seq))
                continue;
            AddFrame(assembler, key, highlight, ringType: !ringed);
            ringed = true;
        }
    }

    private static void AddFrame(MessageAssembler assembler, FrameKey key, HexHighlight highlight, bool ringType)
    {
        if (!assembler.TryGetFrame(key.Direction, key.Seq, out FrameRecord frame))
            return;
        highlight.Frames.Add(key);
        if (ringType && frame.Kind == FrameKind.Frame)
            highlight.Selected.Add(new HexRange(key.Direction, frame.Offset, frame.Offset + 1));
    }

    private bool TryFindField(CaptureSession session, in FrameRecord frame, long offset, out FieldHover hover)
    {
        hover = FieldHover.None;
        MessageRef message = session.Assembler.GetMessage(frame.Direction, frame.Seq);
        long payload = PayloadOffset(session, frame, offset);
        if (payload < 0 || Decode(session, frame, message) is not { } value || value.Range.Message != message)
            return false;

        long local = payload - value.Range.Offset;
        if (local < 0 || local >= value.Bytes.Length)
            return false;

        int index = value.FindField((int)local);
        if (index < 0)
            return false;

        DecodedField field = value.Fields[index];
        hover = new FieldHover(message, index, value.Range.Offset + field.Offset, field.Length);
        return true;
    }

    // The byte's offset in its message's payload coordinates (see PayloadRange); -1 outside the body.
    private long PayloadOffset(CaptureSession session, in FrameRecord frame, long offset)
    {
        if (offset < frame.BodyOffset)
            return -1;

        MessageRef message = session.Assembler.GetMessage(frame.Direction, frame.Seq);
        if (message.Kind == MessageRefKind.Frame)
            return offset - frame.BodyOffset;

        _fragments.Clear();
        session.Assembler.GetFragments(message, _fragments);
        foreach (TraceFragment fragment in _fragments)
        {
            if (fragment.Direction == frame.Direction && fragment.Seq == frame.Seq && fragment.Length > 0
                && offset >= fragment.StreamOffset && offset < fragment.StreamOffset + fragment.Length)
            {
                return fragment.MessageOffset + (offset - fragment.StreamOffset);
            }
        }

        return -1;
    }

    // The decoded value a message's bytes belong to: an invocation's arguments or its result.
    private DecodedValue? Decode(CaptureSession session, in FrameRecord frame, in MessageRef message)
    {
        MessageAssembler assembler = session.Assembler;
        if (ReferenceEquals(session, _cachedSession) && message == _cachedMessage && assembler.Version == _cachedVersion)
            return _cachedValue;

        DecodedValue? value = null;
        FrameLink link = assembler.GetLink(frame.Direction, frame.Seq);
        if (link.Entity == EntityKind.Invocation && (uint)link.EntityIndex < (uint)assembler.Invocations.Length)
        {
            InvocationRow invocation = assembler.Invocations[link.EntityIndex];
            if (invocation.Arguments.Message == message)
                value = session.Decoder.DecodeArguments(invocation);
            else if (invocation.Value.Message == message)
                value = session.Decoder.DecodeResult(invocation);
        }

        _cachedSession = session;
        _cachedMessage = message;
        _cachedVersion = assembler.Version;
        _cachedValue = value;
        return value;
    }
}
