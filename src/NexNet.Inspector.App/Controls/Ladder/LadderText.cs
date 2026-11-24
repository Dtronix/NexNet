using System.Globalization;
using NexNet.Inspector.App.Model;
using NexNet.Inspector.Core.Assembly;
using NexNet.Inspector.Core.Framing;
using NexNet.Inspector.Core.Model;
using NexNet.Messages;

namespace NexNet.Inspector.App.Controls.Ladder;

/// <summary>The words a ladder row shows and announces, formatted when the row comes into view.</summary>
public static class LadderText
{
    /// <summary>The name of a message type as the protocol spells it; null for an unnamed byte.</summary>
    public static string? TypeName(MessageType type) => type switch
    {
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
        _ => null,
    };

    /// <summary>The bold title: the type, with a chunk's number ("MessageChunk 2/5").</summary>
    public static string Title(in LadderRow row, int chunkTotal)
    {
        if (row.Kind == FrameKind.Preamble) return "Preamble";
        if (row.Kind == FrameKind.Malformed) return "Malformed 0x" + ((byte)row.Type).ToString("X2", CultureInfo.InvariantCulture);
        if (row.Type == MessageType.Unset) return "Frame";
        string name = TypeName(row.Type) ?? "Type 0x" + ((byte)row.Type).ToString("X2", CultureInfo.InvariantCulture);
        return row.Part > 0
            ? name + " " + row.Part.ToString(CultureInfo.InvariantCulture) + "/" + Math.Max(row.Part, chunkTotal).ToString(CultureInfo.InvariantCulture)
            : name;
    }

    /// <summary>
    /// The target ("inv 812", "pipe 3·1", "chan 4·2") with the method or channel name, the note, and what the body adds
    /// ("NnP␔ · v1", "auth token · redacted", "clientId 0x3A21", "+1", "Ready"); empty for none.
    /// </summary>
    public static string Detail(in LadderRow row, string? name)
    {
        string target = row.Target switch
        {
            LadderTarget.Invocation => "inv " + row.TargetId.ToString(CultureInfo.InvariantCulture),
            LadderTarget.Pipe or LadderTarget.Channel or LadderTarget.Collection => PipeText.Noun(PipeKind(row) ?? PipeContent.Raw) + " " + PipeIds(row.TargetId),
            _ => "",
        };
        // "chan 36·36 LogLine ×2": what of the channel the frame carried.
        if (row.Target is LadderTarget.Channel or LadderTarget.Collection) name = PipeText.WithItems(name, row.Items, row.ItemPartial);
        if (name is not null) target = target.Length == 0 ? name : target + " " + name;
        string? note = Note(row.Note);
        if (note is not null) target = target.Length == 0 ? note : target + " · " + note;
        return FrameDetailText.Join(target, null, FrameDetailText.Words(row.Detail, row.Flags));
    }

    /// <summary>What the row's pipe carries: the chip after the title and the glyph before the target; null for a row without a pipe.</summary>
    public static PipeContent? PipeKind(in LadderRow row) => row.Target switch
    {
        LadderTarget.Pipe => PipeContent.Raw,
        LadderTarget.Channel => PipeContent.Channel,
        LadderTarget.Collection => PipeContent.Collection,
        _ => null,
    };

    /// <summary>The byte count ("4,096 B"); null when the row shows none.</summary>
    public static string? Bytes(in LadderRow row) =>
        row.Bytes < 0 ? null : row.Bytes.ToString("N0", CultureInfo.InvariantCulture) + " B";

    /// <summary>The Time column: milliseconds since <paramref name="origin"/> ("1,262.418").</summary>
    public static string Time(long ticks, long origin, long frequency) =>
        ((ticks - origin) * 1000.0 / Math.Max(1, frequency)).ToString("N3", CultureInfo.InvariantCulture);

    /// <summary>The note's words; null for none.</summary>
    public static string? Note(LadderNote note) => note switch
    {
        LadderNote.Argument => "Arg",
        LadderNote.Result => "Result",
        LadderNote.Completed => "Completed",
        LadderNote.Exception => "Exception",
        LadderNote.Unauthorized => "Unauthorized",
        LadderNote.Canceled => "Canceled",
        LadderNote.Partial => "partial",
        _ => null,
    };

    /// <summary>
    /// What a screen reader announces for a row (ladder-row.md): "1,262.418 ms inbound MessageChunk 2/5 invocation 812,
    /// 4,096 bytes, transport split".
    /// </summary>
    public static string Describe(in LadderRow row, int chunkTotal, string? name, long origin, long frequency)
    {
        string text = Time(row.Ticks, origin, frequency) + " ms " + (row.Direction == TapDirection.Inbound ? "inbound " : "outbound ")
            + Title(row, chunkTotal);
        text += row.Target switch
        {
            LadderTarget.Invocation => " invocation " + row.TargetId.ToString(CultureInfo.InvariantCulture),
            LadderTarget.Pipe => " pipe " + PipeIds(row.TargetId),
            LadderTarget.Channel => " channel " + PipeIds(row.TargetId),
            LadderTarget.Collection => " collection " + PipeIds(row.TargetId),
            _ => "",
        };
        if (name is not null) text += " " + name;
        if (Note(row.Note) is { } note) text += ", " + note;
        if (FrameDetailText.Words(row.Detail, row.Flags) is { } detail) text += ", " + detail;
        if (row.Bytes >= 0) text += ", " + row.Bytes.ToString("N0", CultureInfo.InvariantCulture) + " bytes";
        foreach (FrameFlags flag in FlagChip.DisplayOrder)
        {
            if ((row.Flags & flag) != 0) text += ", " + FlagWords(flag);
        }

        return text;
    }

    // The client's local id first, as the timeline and the package write it ("Pipe 3·1").
    private static string PipeIds(ushort id) =>
        (id & 0xFF).ToString(CultureInfo.InvariantCulture) + "·" + (id >> 8).ToString(CultureInfo.InvariantCulture);

    private static string FlagWords(FrameFlags flag) => flag switch
    {
        FrameFlags.TransportSplit => "transport split",
        FrameFlags.NexNetSplit => "NexNet split",
        FrameFlags.Cancel => "cancellation",
        FrameFlags.Redacted => "redacted",
        _ => "partial",
    };
}
