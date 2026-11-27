using NexNet.Inspector.Core.Framing;
using NexNet.Inspector.Core.Model;

namespace NexNet.Inspector.Core.Assembly;

public sealed partial class MessageAssembler
{
    /// <summary>
    /// Lists the fragments of a message in stream order: which frames carried which of its bytes. MessageStart and
    /// MessageAbort frames appear with length 0; a receiver's abort appears in the other direction.
    /// </summary>
    /// <param name="message">The message.</param>
    /// <param name="destination">Receives the fragments; it is not cleared first.</param>
    /// <returns>The number of fragments added.</returns>
    public int GetFragments(in MessageRef message, List<TraceFragment> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (message.Kind == MessageRefKind.Frame)
        {
            if (!TryGetFrame(message.Direction, message.Seq, out var frame))
                return 0;

            destination.Add(new TraceFragment(frame.Direction, frame.Seq, 0, frame.BodyOffset, frame.BodyLength,
                frame.FirstOpId, frame.LastOpId));
            return 1;
        }

        if (!TryGetChain(message, out var first, out _))
            return 0;

        var added = 0;
        for (var f = first; f >= 0; f = _fragments[f].Next)
        {
            var fragment = _fragments[f];
            TryGetFrame(fragment.Direction, fragment.Seq, out var frame);
            destination.Add(new TraceFragment(fragment.Direction, fragment.Seq, fragment.MessageOffset,
                fragment.StreamOffset, fragment.Length, frame.FirstOpId, frame.LastOpId));
            added++;
        }

        return added;
    }

    /// <summary>
    /// Lists the frames of the message's direction that lie between its first and last fragment but carry none of
    /// it: what the message was interleaved with on the wire.
    /// </summary>
    /// <param name="message">The message.</param>
    /// <param name="destination">Receives the frames in stream order; it is not cleared first.</param>
    /// <returns>The number of frames added.</returns>
    public int GetInterleaved(in MessageRef message, List<FrameRecord> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (message.Kind == MessageRefKind.Frame || !TryGetChain(message, out var first, out _))
            return 0;

        var direction = message.Direction;
        long last = -1;
        for (var f = first; f >= 0; f = _fragments[f].Next)
        {
            if (_fragments[f].Direction == direction)
                last = _fragments[f].Seq;
        }

        // Fragments of one direction are in ascending sequence order, so a single walk skips the message's own frames.
        var frames = GetFrames(direction);
        var next = NextOwnFragment(first, direction);
        var added = 0;
        for (var seq = message.Seq; seq <= last; seq++)
        {
            if (next >= 0 && _fragments[next].Seq == seq)
            {
                while (next >= 0 && _fragments[next].Seq == seq)
                    next = NextOwnFragment(_fragments[next].Next, direction);
                continue;
            }

            var index = IndexOf(direction, seq);
            if (index >= 0)
            {
                destination.Add(frames[index]);
                added++;
            }
        }

        return added;
    }

    private int NextOwnFragment(int fragment, TapDirection direction)
    {
        while (fragment >= 0 && _fragments[fragment].Direction != direction)
            fragment = _fragments[fragment].Next;
        return fragment;
    }
}
