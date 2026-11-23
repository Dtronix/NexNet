using NexNet.Inspector.Core.Assembly;
using NexNet.Inspector.Core.Model;

namespace NexNet.Inspector.App.Controls.PropertyTree;

/// <summary>
/// Maps a message's payload coordinates to its direction's stream: a single-frame message's payload is its frame body
/// (one offset); an assembled message or a channel item lies in fragments (<see cref="MessageAssembler.GetFragments"/>).
/// </summary>
public readonly struct PayloadMap
{
    private readonly long _bodyOffset;
    private readonly TraceFragment[]? _fragments;

    private PayloadMap(MessageRef message, long bodyOffset, TraceFragment[]? fragments)
    {
        Message = message;
        _bodyOffset = bodyOffset;
        _fragments = fragments;
    }

    /// <summary>The message mapped.</summary>
    public MessageRef Message { get; }

    /// <summary>A frame's own coordinates: offset 0 is its first body byte.</summary>
    /// <param name="direction">The frame's direction.</param>
    /// <param name="seq">The frame.</param>
    /// <param name="bodyOffset">The stream offset of its body.</param>
    public static PayloadMap ForFrame(TapDirection direction, long seq, long bodyOffset) =>
        new(MessageRef.ForFrame(direction, seq), bodyOffset, null);

    /// <summary>A message carried by fragments, in message order.</summary>
    /// <param name="message">The message.</param>
    /// <param name="fragments">Its fragments (<see cref="MessageAssembler.GetFragments"/>).</param>
    public static PayloadMap ForFragments(MessageRef message, TraceFragment[] fragments) => new(message, 0, fragments);

    /// <summary>The bytes of a payload range: its message coordinates and its stream range.</summary>
    /// <param name="offset">The first byte in message coordinates.</param>
    /// <param name="length">The byte count.</param>
    public PropertyBytes Map(long offset, int length)
    {
        if (length <= 0 || Message.IsNone) return PropertyBytes.None;
        if (_fragments is null)
            return new PropertyBytes(Message, offset, length, Message.Direction, new StreamRange(_bodyOffset + offset, length));

        foreach (TraceFragment fragment in _fragments)
        {
            if (fragment.Length <= 0 || offset < fragment.MessageOffset || offset >= fragment.MessageOffset + fragment.Length) continue;

            // A field that runs into the next fragment selects only this fragment's part: the stream range between
            // the two holds other frames.
            long inFragment = offset - fragment.MessageOffset;
            long part = Math.Min(length, fragment.Length - inFragment);
            return new PropertyBytes(Message, offset, length, fragment.Direction, new StreamRange(fragment.StreamOffset + inFragment, part));
        }

        return new PropertyBytes(Message, offset, length, Message.Direction, default);
    }
}
