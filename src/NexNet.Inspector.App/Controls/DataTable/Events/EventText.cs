using System.Globalization;
using NexNet.Inspector.App.Model;
using NexNet.Inspector.Core.Assembly;
using NexNet.Inspector.Core.Framing;
using NexNet.Messages;

namespace NexNet.Inspector.App.Controls.DataTable.Events;

/// <summary>The Events columns' text (data-table.md: numbers with thousands separators, ms with 3 decimals).</summary>
public static class EventText
{
    /// <summary>The Seq column: <c>18,229</c>.</summary>
    public static string Number(in EventRow row) => row.Number.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>The Time ms column: <c>1,259.880</c>.</summary>
    public static string Time(in EventRow row) => row.TimeMs.ToString("N3", CultureInfo.InvariantCulture);

    /// <summary>The Bytes column: <c>4,096</c>.</summary>
    public static string Bytes(in EventRow row) => row.Length.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>The Type column: the message type's name, a chunk's number (<c>MessageChunk 1/5</c>), or the record kind.</summary>
    public static string Type(in EventRow row)
    {
        if (row.Kind == FrameKind.Preamble) return "Preamble";
        if (row.Kind == FrameKind.Malformed) return "Malformed";
        string name = TypeName(row.Type);
        if (row.Type != MessageType.MessageChunk || row.ChunkNumber <= 0) return name;
        string number = row.ChunkNumber.ToString(CultureInfo.InvariantCulture);
        return row.ChunkCount > 0
            ? name + " " + number + "/" + row.ChunkCount.ToString(CultureInfo.InvariantCulture)
            : name + " " + number;
    }

    /// <summary>
    /// The Target column: the target with its name and what the body adds (<c>inv 812 SendAttachment</c>,
    /// <c>inv 1 · Completed</c>, <c>pipe 1·1 Users · Ready</c>, <c>chan 4·2 LogLine</c>, <c>NnP␔ · v1</c>,
    /// <c>auth token · redacted</c>, <c>clientId 0x3A21</c>, <c>+1</c>); empty for none.
    /// </summary>
    public static string Target(in EventRow row)
    {
        string target = row.Target switch
        {
            EventTargetKind.Invocation => "inv " + row.TargetId.ToString(CultureInfo.InvariantCulture),
            EventTargetKind.Pipe or EventTargetKind.Channel or EventTargetKind.Collection => PipeText.Noun(PipeKind(row)) + " " + PipeIds(row.TargetId),
            _ => "",
        };
        string? name = row.Target switch
        {
            // A result or a large message's start names its state or kind instead, as the package's rows do.
            EventTargetKind.Invocation => row.Detail.Kind == FrameDetailKind.None ? row.MethodName : null,
            EventTargetKind.Pipe => row.PipeLabel,
            // "LogLine ×2", "LogLine (part)": what of the channel the frame carried.
            EventTargetKind.Channel or EventTargetKind.Collection => PipeText.WithItems(row.PipeLabel, row.ItemCount, row.ItemPartial),
            _ => null,
        };
        return FrameDetailText.Join(target, name, FrameDetailText.Words(row.Detail, row.Flags));
    }

    /// <summary>What the frame's pipe carries: the CH / CO chip beside the type; <see cref="PipeContent.Raw"/> for no chip.</summary>
    public static PipeContent PipeKind(in EventRow row) => row.Target switch
    {
        EventTargetKind.Channel => PipeContent.Channel,
        EventTargetKind.Collection => PipeContent.Collection,
        _ => PipeContent.Raw,
    };

    /// <summary>What assistive technology announces for a row: type (and the channel or collection it carries), direction and time.</summary>
    public static string Accessible(in EventRow row) =>
        Type(row) + row.Target switch
        {
            EventTargetKind.Channel => " on a channel",
            EventTargetKind.Collection => " on a collection",
            _ => "",
        } + (row.Direction == Core.Model.TapDirection.Inbound ? ", inbound, " : ", outbound, ") + Time(row) + " ms";

    /// <summary>The name of a type byte; an unassigned disconnect code or an unknown byte by its value.</summary>
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
        >= (MessageType)20 and <= (MessageType)39 => "Disconnect " + ((byte)type).ToString(CultureInfo.InvariantCulture),
        _ => "0x" + ((byte)type).ToString("X2", CultureInfo.InvariantCulture),
    };

    private static string PipeIds(ushort id) =>
        (id & 0xFF).ToString(CultureInfo.InvariantCulture) + "·" + (id >> 8).ToString(CultureInfo.InvariantCulture);
}
