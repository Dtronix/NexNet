using System.Globalization;
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

    /// <summary>The Target column: <c>inv 812 SendAttachment</c>, <c>pipe 3·1</c>, <c>chan 4·2</c>; empty for none.</summary>
    public static string Target(in EventRow row) => row.Target switch
    {
        EventTargetKind.Invocation => row.MethodName is { } method
            ? "inv " + row.TargetId.ToString(CultureInfo.InvariantCulture) + " " + method
            : "inv " + row.TargetId.ToString(CultureInfo.InvariantCulture),
        EventTargetKind.Pipe => "pipe " + PipeIds(row.TargetId),
        EventTargetKind.Channel => "chan " + PipeIds(row.TargetId),
        _ => "",
    };

    /// <summary>What assistive technology announces for a row: type, direction and time.</summary>
    public static string Accessible(in EventRow row) =>
        Type(row) + (row.Direction == Core.Model.TapDirection.Inbound ? ", inbound, " : ", outbound, ") + Time(row) + " ms";

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
