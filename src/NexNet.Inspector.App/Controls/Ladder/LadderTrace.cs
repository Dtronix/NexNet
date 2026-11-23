using NexNet.Inspector.App.Model;
using NexNet.Inspector.Core.Assembly;
using NexNet.Inspector.Core.Session;

namespace NexNet.Inspector.App.Controls.Ladder;

/// <summary>
/// What the ladder's trace bracket joins: every row of one entity, message or frame, and the rows from the first to the
/// last of them (the bracket's span, in row indices). Rows inside the span that are not part of it are interleaved.
/// </summary>
/// <param name="Kind">What is traced.</param>
/// <param name="Entity">The entity list, for <see cref="LadderTraceKind.Entity"/>.</param>
/// <param name="Index">The entity's or message's index.</param>
/// <param name="Frame">The frame, for <see cref="LadderTraceKind.Frame"/>.</param>
/// <param name="First">The first traced row; -1 when no row is traced.</param>
/// <param name="Last">The last traced row.</param>
public readonly record struct LadderTrace(LadderTraceKind Kind, EntityKind Entity, int Index, FrameKey Frame, int First, int Last)
{
    /// <summary>Nothing traced.</summary>
    public static LadderTrace None => new(LadderTraceKind.None, EntityKind.None, -1, default, -1, -1);

    /// <summary>The traced message's frames, for <see cref="LadderTraceKind.Members"/>.</summary>
    public MessageTrace? Members { get; init; }

    /// <summary>True when at least one row is traced.</summary>
    public bool IsActive => Kind != LadderTraceKind.None && First >= 0;

    /// <summary>True when <paramref name="row"/> carries part of what is traced.</summary>
    public bool Contains(in LadderRow row) => Kind switch
    {
        LadderTraceKind.Entity => row.Entity == Entity && row.EntityIndex == Index,
        LadderTraceKind.Message => row.Message == Index,
        LadderTraceKind.Frame => row.Frame == Frame,
        LadderTraceKind.Members => Members?.Contains(row.Frame) ?? false,
        _ => false,
    };

    /// <summary>How the row at <paramref name="index"/> takes part in the bracket.</summary>
    public LadderBracket BracketOf(int index, in LadderRow row) =>
        !IsActive || index < First || index > Last ? LadderBracket.None
        : Contains(row) ? LadderBracket.Member
        : LadderBracket.Interleaved;
}
