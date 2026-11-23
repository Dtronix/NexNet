using NexNet.Inspector.Core.Assembly;
using NexNet.Inspector.Core.Framing;
using NexNet.Inspector.Core.Model;
using NexNet.Inspector.Core.Session;
using NexNet.Messages;

namespace NexNet.Inspector.App.Controls.Ladder;

/// <summary>
/// One ladder row: a frame as the ladder draws it. Holds numbers and enums only, so a long capture costs no string per
/// frame; a row's text is formatted when it scrolls into view (<see cref="LadderText"/>).
/// </summary>
/// <param name="Frame">The frame.</param>
/// <param name="Ticks">Stopwatch ticks of the op that carried the frame's first byte.</param>
/// <param name="Kind">Frame, preamble or unframeable bytes.</param>
/// <param name="Type">The type byte.</param>
/// <param name="Flags">The frame's chips.</param>
/// <param name="Bytes">The byte count shown: the body for a frame, the whole record for the preamble or malformed bytes; -1 for none.</param>
/// <param name="Target">What <paramref name="TargetId"/> names.</param>
/// <param name="TargetId">Invocation id, or pipe id in its body form (<c>clientId | serverId &lt;&lt; 8</c>).</param>
/// <param name="Note">The note after the target.</param>
/// <param name="Name">A label id (<see cref="LadderModel.GetLabel"/>): the invocation's method name; -1 for none.</param>
/// <param name="FirstOpId">The op that carried the first byte.</param>
/// <param name="LastOpId">The op that carried the last byte.</param>
/// <param name="Entity">The entity the frame belongs to.</param>
/// <param name="EntityIndex">Index of that entity; -1 for none.</param>
/// <param name="Message">Index of the assembled message the frame is part of; -1 for none.</param>
/// <param name="Part">For a MessageChunk, its 1-based number among the message's chunks; 0 otherwise.</param>
/// <param name="Detail">What the body adds beyond target and note (preamble version, greeting token or ID, credits, pipe state).</param>
public readonly record struct LadderRow(
    FrameKey Frame,
    long Ticks,
    FrameKind Kind,
    MessageType Type,
    FrameFlags Flags,
    int Bytes,
    LadderTarget Target,
    ushort TargetId,
    LadderNote Note,
    int Name,
    long FirstOpId,
    long LastOpId,
    EntityKind Entity,
    int EntityIndex,
    int Message,
    int Part,
    FrameDetail Detail = default)
{
    /// <summary>The direction: inbound arrows point right, at the tapped endpoint.</summary>
    public TapDirection Direction => Frame.Direction;
}
