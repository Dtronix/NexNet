using System.Buffers;
using System.Buffers.Binary;
using NexNet.Inspector.Core.Assembly;
using NexNet.Inspector.Core.Framing;
using NexNet.Messages;

namespace NexNet.Inspector.Tap;

/// <summary>A transport op held back until the greeting it carries part of is complete.</summary>
/// <param name="Bytes">A copy of the op, with any token bytes already replaced by the marker.</param>
/// <param name="Ticks">When the op happened.</param>
/// <param name="RedactionStart">Start of the redacted range within the op; -1 when none.</param>
/// <param name="RedactionLength">Length of the redacted range.</param>
internal readonly record struct HeldOp(byte[] Bytes, long Ticks, int RedactionStart, int RedactionLength);

/// <summary>
/// Finds the auth token in the client-to-server stream of one session and redacts it before any viewer or recorder sees
/// the bytes.
/// </summary>
/// <remarks>
/// The stream starts with the 8-byte preamble and then the ClientGreeting (or ClientGreetingReconnection) frame, whose
/// token can be split across transport ops. Ops are copied and held until the greeting frame is complete, the token
/// range is found with <see cref="MessageAssembler.TryFindGreetingToken"/>, and the held ops are released with
/// the token bytes replaced and the range noted per op. The greeting is pre-handshake and at most one frame
/// (65 546 bytes with the preamble), so holding it is cheap; afterwards the redactor is dropped and the direction
/// costs nothing. A greeting body that does not parse is redacted whole: when in doubt, the secret stays out.
/// Used from one direction's thread only.
/// </remarks>
internal sealed class GreetingRedactor
{
    private const int HeaderEnd = FrameLayout.PreambleLength + 3;

    private readonly List<HeldOp> _held = [];
    private int _length;

    /// <summary>Holds a copy of the op and, once the greeting is complete (or the stream turns out to carry none), releases every held op.</summary>
    /// <returns>True when <paramref name="released"/> holds the ops to deliver, in order; the redactor is then done.</returns>
    public bool Push(in ReadOnlySequence<byte> data, long ticks, out List<HeldOp> released)
    {
        released = _held;
        if (!data.IsEmpty)
        {
            _held.Add(new HeldOp(data.ToArray(), ticks, -1, 0));
            _length += (int)data.Length;
        }

        if (_length < HeaderEnd)
            return false;

        Span<byte> head = stackalloc byte[HeaderEnd];
        CopyOut(0, head);
        if (!head.StartsWith(FrameLayout.PreambleMagic))
            return true;

        var type = (MessageType)head[FrameLayout.PreambleLength];
        if (type is not (MessageType.ClientGreeting or MessageType.ClientGreetingReconnection))
            return true;

        var bodyLength = BinaryPrimitives.ReadUInt16LittleEndian(head.Slice(FrameLayout.PreambleLength + 1));
        if (_length < HeaderEnd + bodyLength)
            return false;

        var body = new byte[bodyLength];
        CopyOut(HeaderEnd, body);
        int start, length;
        if (MessageAssembler.TryFindGreetingToken(body, out var offset, out var tokenLength))
        {
            if (tokenLength <= 0)
                return true;
            start = HeaderEnd + offset;
            length = tokenLength;
        }
        else
        {
            start = HeaderEnd;
            length = bodyLength;
        }

        var marker = new byte[length];
        InspectorRedaction.WriteMarker(body.AsSpan(start - HeaderEnd, length), marker);
        Patch(start, marker);
        return true;
    }

    private void CopyOut(int offset, Span<byte> destination)
    {
        var at = 0;
        foreach (var op in _held)
        {
            if (destination.IsEmpty)
                return;
            var end = at + op.Bytes.Length;
            if (offset < end)
            {
                var from = offset - at;
                var n = Math.Min(destination.Length, op.Bytes.Length - from);
                op.Bytes.AsSpan(from, n).CopyTo(destination);
                destination = destination.Slice(n);
                offset += n;
            }

            at = end;
        }
    }

    private void Patch(int start, ReadOnlySpan<byte> marker)
    {
        var end = start + marker.Length;
        var at = 0;
        for (var i = 0; i < _held.Count; i++)
        {
            var op = _held[i];
            var opEnd = at + op.Bytes.Length;
            var from = Math.Max(start, at);
            var to = Math.Min(end, opEnd);
            if (from < to)
            {
                marker.Slice(from - start, to - from).CopyTo(op.Bytes.AsSpan(from - at));
                _held[i] = op with { RedactionStart = from - at, RedactionLength = to - from };
            }

            at = opEnd;
        }
    }
}
