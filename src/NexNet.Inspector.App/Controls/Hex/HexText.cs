using System.Globalization;
using System.Text;
using NexNet.Inspector.App.Model;
using NexNet.Inspector.Core.Assembly;
using NexNet.Inspector.Core.Framing;
using NexNet.Inspector.Core.Model;
using NexNet.Inspector.Core.Session;
using NexNet.Messages;

namespace NexNet.Inspector.App.Controls.Hex;

/// <summary>
/// The hex view's text columns (mono): the offset (at least six uppercase hex digits, as wide as the largest offset
/// shown), three spaces, sixteen two-character cells one space apart, two spaces, the sixteen ASCII characters. A byte
/// line is written into a caller's buffer; only annotation lines become strings (one per visible annotation).
/// </summary>
public static class HexText
{
    /// <summary>Fewest offset digits (PKG: "1A3F00").</summary>
    public const int MinDigits = 6;

    /// <summary>Spaces between the offset column and the first cell.</summary>
    public const int OffsetGap = 3;

    /// <summary>Characters per cell: two hex digits and a separating space.</summary>
    public const int CellStride = 3;

    /// <summary>Most frame starts named on one annotation line before "+n more".</summary>
    public const int MaxNamedFrames = 4;

    private const string Hex = "0123456789ABCDEF";

    /// <summary>The offset column's digit count for offsets below <paramref name="maxOffset"/>.</summary>
    public static int OffsetDigits(long maxOffset)
    {
        int digits = 1;
        for (long v = Math.Max(0, maxOffset - 1); v >= 16; v >>= 4)
            digits++;
        return Math.Max(MinDigits, digits);
    }

    /// <summary>Column of a cell's first hex digit.</summary>
    public static int CellColumn(int digits, int cell) => digits + OffsetGap + (cell * CellStride);

    /// <summary>Column of the first ASCII character.</summary>
    public static int AsciiColumn(int digits) => CellColumn(digits, HexLayout.BytesPerRow) + 1;

    /// <summary>Characters in a byte line.</summary>
    public static int LineLength(int digits) => AsciiColumn(digits) + HexLayout.BytesPerRow;

    /// <summary>The ASCII column's character for a byte: printable ASCII as itself, anything else a middle dot.</summary>
    public static char AsciiOf(byte value) => value is >= 0x20 and < 0x7F ? (char)value : '·';

    /// <summary>Writes a byte line into <paramref name="destination"/>, which must hold <see cref="LineLength"/> chars.</summary>
    public static void FormatBytes(in HexRow row, int digits, Span<char> destination)
    {
        destination.Slice(0, LineLength(digits)).Fill(' ');
        long offset = row.Row;
        for (int i = digits - 1; i >= 0; i--)
        {
            destination[i] = Hex[(int)(offset & 0xF)];
            offset >>= 4;
        }

        ReadOnlySpan<byte> bytes = row.Bytes;
        ReadOnlySpan<HexCellMarks> marks = row.Marks;
        int ascii = AsciiColumn(digits);
        for (int cell = row.First; cell < row.First + row.Count; cell++)
        {
            int at = CellColumn(digits, cell);
            HexCellMarks mark = marks[cell];
            if ((mark & HexCellMarks.Unavailable) != 0)
                continue;
            if ((mark & HexCellMarks.Redacted) != 0)
            {
                destination[at] = '·';
                destination[at + 1] = '·';
                destination[ascii + cell] = '░';
                continue;
            }

            byte value = bytes[cell];
            destination[at] = Hex[value >> 4];
            destination[at + 1] = Hex[value & 0xF];
            destination[ascii + cell] = AsciiOf(value);
        }
    }

    /// <summary>The text of a read-rule line (after its indent): "┃ read #A → #B · n B · t ms".</summary>
    public static string Rule(HexLayout layout, in HexLine line)
    {
        layout.GetReadStarts(line, out int first, out int count);
        if (first < 0)
            return "┃";

        HexOp op = layout.GetOp(first);
        StringBuilder text = new("┃ ");
        string verb = op.Direction == TapDirection.Inbound ? "read" : "write";
        HexOp? previous = first > 0 ? layout.GetOp(first - 1) : null;
        CultureInfo invariant = CultureInfo.InvariantCulture;
        if (!op.RunStart && previous is { } before)
        {
            text.Append(verb).Append(" #").Append(before.OpId.ToString("N0", invariant))
                .Append(" → #").Append(op.OpId.ToString("N0", invariant));
        }
        else
        {
            text.Append(op.Direction == TapDirection.Inbound ? "▼ " : "▲ ").Append(verb).Append(" #")
                .Append(op.OpId.ToString("N0", invariant));
            if (previous is { } prior && prior.Direction == op.Direction && op.Offset > prior.End)
                text.Append(" · after a gap of ").Append((op.Offset - prior.End).ToString("N0", invariant)).Append(" B");
        }

        text.Append(" · ").Append(op.Length.ToString("N0", invariant)).Append(" B");
        if (count > 1)
            text.Append(" (+").Append((count - 1).ToString(invariant)).Append(" more ").Append(verb).Append(count == 2 ? ")" : "s)");
        if (layout.Session is { } session)
            text.Append(" · ").Append(Milliseconds(session, op.Ticks)).Append(" ms");
        return text.ToString();
    }

    /// <summary>The text of a frame annotation line: "▸ Type #seq @offset" for each frame starting in the row.</summary>
    public static string Frames(HexLayout layout, in HexLine line)
    {
        int count = layout.GetFrameStarts(line, out int first);
        if (count == 0 || layout.Session is not { } session)
            return "";

        ReadOnlySpan<FrameRecord> frames = session.Assembler.GetFrames(line.Direction);
        StringBuilder text = new();
        CultureInfo invariant = CultureInfo.InvariantCulture;
        int named = Math.Min(count, MaxNamedFrames);
        for (int i = 0; i < named; i++)
        {
            ref readonly FrameRecord frame = ref frames[first + i];
            if (i > 0)
                text.Append("   ");
            text.Append("▸ ").Append(TypeName(frame)).Append(" #").Append(frame.Seq.ToString("N0", invariant));
            AppendPipe(text, session, frame);
            text.Append(" @").Append(frame.Offset.ToString("X", invariant));
        }

        if (count > named)
            text.Append("   +").Append((count - named).ToString(invariant)).Append(" more");
        return text.ToString();
    }

    // A pipe frame names what rides on its pipe: "≋ LogLine" for a channel, "☰ Users" for a collection, "⇄ 3·1" for a raw pipe.
    private static void AppendPipe(StringBuilder text, CaptureSession session, in FrameRecord frame)
    {
        MessageAssembler assembler = session.Assembler;
        FrameLink link = assembler.GetLink(frame.Direction, frame.Seq);
        if (link.Entity != EntityKind.Pipe || (uint)link.EntityIndex >= (uint)assembler.Pipes.Length) return;
        PipeRow pipe = assembler.Pipes[link.EntityIndex];
        string? label = pipe.Content == PipeContent.Raw ? null : session.Decoder.GetPipeLabel(link.EntityIndex);
        text.Append(' ').Append(PipeText.Glyph(pipe.Content)).Append(' ').Append(label ?? PipeText.Ids(pipe.PipeId));
    }

    /// <summary>A frame's display name: its message type, or Preamble / Malformed.</summary>
    public static string TypeName(in FrameRecord frame) => frame.Kind switch
    {
        FrameKind.Preamble => "Preamble",
        FrameKind.Malformed => "Malformed",
        _ => TypeName(frame.Type),
    };

    /// <summary>A message type's name, without reflection (Native AOT).</summary>
    public static string TypeName(MessageType type) => type switch
    {
        MessageType.Unset => "Incomplete",
        MessageType.Ping => "Ping",
        MessageType.DisconnectSocketError => "DisconnectSocketError",
        MessageType.DisconnectGraceful => "DisconnectGraceful",
        MessageType.DisconnectProtocolError => "DisconnectProtocolError",
        MessageType.DisconnectTimeout => "DisconnectTimeout",
        MessageType.DisconnectClientMismatch => "DisconnectClientMismatch",
        MessageType.DisconnectServerMismatch => "DisconnectServerMismatch",
        MessageType.DisconnectServerShutdown => "DisconnectServerShutdown",
        MessageType.DisconnectAuthentication => "DisconnectAuthentication",
        MessageType.DisconnectServerRestarting => "DisconnectServerRestarting",
        MessageType.DisconnectSocketClosedWhenWriting => "DisconnectSocketClosedWhenWriting",
        MessageType.DisconnectRateLimited => "DisconnectRateLimited",
        MessageType.DisconnectUnauthorized => "DisconnectUnauthorized",
        MessageType.DisconnectFlowControl => "DisconnectFlowControl",
        MessageType.DisconnectStalled => "DisconnectStalled",
        MessageType.DuplexPipeWrite => "DuplexPipeWrite",
        MessageType.PipeWindowUpdate => "PipeWindowUpdate",
        MessageType.ClientGreeting => "ClientGreeting",
        MessageType.ClientGreetingReconnection => "ClientGreetingReconnection",
        MessageType.ServerGreeting => "ServerGreeting",
        MessageType.Invocation => "Invocation",
        MessageType.InvocationCancellation => "InvocationCancellation",
        MessageType.InvocationResult => "InvocationResult",
        MessageType.MessageStart => "MessageStart",
        MessageType.MessageChunk => "MessageChunk",
        MessageType.MessageAbort => "MessageAbort",
        MessageType.InvocationCredit => "InvocationCredit",
        MessageType.DuplexPipeUpdateState => "DuplexPipeUpdateState",
        _ => FrameLayout.IsDisconnect((byte)type) ? "Disconnect" : "Unknown",
    };

    private static string Milliseconds(CaptureSession session, long ticks)
    {
        double ms = (ticks - session.StartTicks) * 1000.0 / Math.Max(1, session.Header.StopwatchFrequency);
        return ms.ToString("N3", CultureInfo.InvariantCulture);
    }
}
