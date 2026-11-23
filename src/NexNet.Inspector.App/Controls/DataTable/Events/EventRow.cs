using NexNet.Inspector.Core.Assembly;
using NexNet.Inspector.Core.Framing;
using NexNet.Inspector.Core.Model;
using NexNet.Inspector.Core.Session;
using NexNet.Messages;

namespace NexNet.Inspector.App.Controls.DataTable.Events;

/// <summary>What an Events row's Target column names.</summary>
public enum EventTargetKind : byte
{
    /// <summary>Nothing (preamble, greetings, ping, credit, disconnect).</summary>
    None = 0,

    /// <summary>An invocation: <c>inv 812</c>, with the method's name when the schema knows it.</summary>
    Invocation = 1,

    /// <summary>A byte pipe: <c>pipe 3·1</c>.</summary>
    Pipe = 2,

    /// <summary>A pipe carrying a channel: <c>chan 4·2</c>.</summary>
    Channel = 3,
}

/// <summary>
/// One row of the Events table: a frame of the session in capture order, with what the columns show (data-table.md:
/// Seq, Time ms, direction, Type, Target, Bytes, Flags) and what selection and the trace tint need. A value read from the
/// session under its gate for a visible row only; the table never holds one per frame.
/// </summary>
public readonly record struct EventRow
{
    /// <summary>The frame's 1-based position among all the session's frames (the Seq column).</summary>
    public long Number { get; init; }

    /// <summary>The frame, as every view selects and links it.</summary>
    public FrameKey Frame { get; init; }

    /// <summary>Stopwatch ticks of the op that carried the frame's first byte.</summary>
    public long Ticks { get; init; }

    /// <summary>Milliseconds from the session's start (the Time ms column).</summary>
    public double TimeMs { get; init; }

    /// <summary>The direction (▼ inbound, ▲ outbound).</summary>
    public TapDirection Direction => Frame.Direction;

    /// <summary>A frame, the preamble or unframeable bytes.</summary>
    public FrameKind Kind { get; init; }

    /// <summary>The type byte.</summary>
    public MessageType Type { get; init; }

    /// <summary>Bytes of the frame in the stream (the Bytes column).</summary>
    public int Length { get; init; }

    /// <summary>The flags, drawn as chips.</summary>
    public FrameFlags Flags { get; init; }

    /// <summary>The transport op that carried the first byte.</summary>
    public long FirstOpId { get; init; }

    /// <summary>The transport op that carried the last byte.</summary>
    public long LastOpId { get; init; }

    /// <summary>The logical message the frame carries (part of): what tracing it follows.</summary>
    public MessageRef Message { get; init; }

    /// <summary>What <see cref="TargetId"/> names.</summary>
    public EventTargetKind Target { get; init; }

    /// <summary>The invocation id, or the pipe id (<c>clientId | serverId &lt;&lt; 8</c>).</summary>
    public ushort TargetId { get; init; }

    /// <summary>The invoked method's name from the schema, for an invocation target; null when unknown.</summary>
    public string? MethodName { get; init; }

    /// <summary>For a MessageChunk, its 1-based number among its message's chunks; 0 otherwise.</summary>
    public int ChunkNumber { get; init; }

    /// <summary>For a MessageChunk of a complete message, how many chunks it had; 0 while unknown.</summary>
    public int ChunkCount { get; init; }

    /// <summary>True when the frame belongs to the traced message (the trace tint, I25 refines what is traced).</summary>
    public bool IsTraced { get; init; }
}
