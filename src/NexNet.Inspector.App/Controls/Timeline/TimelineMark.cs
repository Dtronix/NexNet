using NexNet.Inspector.Core.Assembly;
using NexNet.Inspector.Core.Model;

namespace NexNet.Inspector.App.Controls.Timeline;

/// <summary>Names a lane across model refreshes: pins, hover and the height animation follow it.</summary>
/// <param name="Kind">What the lane shows.</param>
/// <param name="Index">The row, pipe or collection it shows; 0 for the fixed lanes.</param>
public readonly record struct LaneKey(LaneKind Kind, int Index);

/// <summary>
/// One mark on a lane: a frame or op block, an invocation span, a pipe's lifetime or open/close mark, or a split mark.
/// Ticks are the capture's Stopwatch ticks; the entity and frame let a click select it and a trace light it.
/// </summary>
/// <param name="Start">First tick.</param>
/// <param name="End">Last tick (equal to <paramref name="Start"/> for an instant).</param>
/// <param name="Kind">How it is drawn.</param>
/// <param name="Direction">The direction whose colour a block takes.</param>
/// <param name="Flags">Modifiers.</param>
/// <param name="Entity">The entity list the mark belongs to (<see cref="EntityKind.None"/> for none).</param>
/// <param name="EntityIndex">Index into that list; -1 for none.</param>
/// <param name="Seq">The frame's sequence number in <paramref name="Direction"/>; -1 when the mark is no frame.</param>
/// <param name="Bytes">Bytes the mark carried (an expanded block shows them); 0 for none.</param>
/// <param name="Label">The mark's text in <see cref="TimelineModel.GetLabel"/>; -1 for none.</param>
public readonly record struct TimelineMark(
    long Start,
    long End,
    MarkKind Kind,
    TapDirection Direction,
    MarkFlags Flags,
    EntityKind Entity,
    int EntityIndex,
    long Seq,
    int Bytes,
    int Label)
{
    /// <summary>A block for frame <paramref name="seq"/>.</summary>
    public static TimelineMark Frame(long start, long end, TapDirection direction, long seq, int bytes, int label,
        EntityKind entity = EntityKind.None, int entityIndex = -1, MarkFlags flags = MarkFlags.None) =>
        new(start, Math.Max(start, end), MarkKind.Block, direction, flags, entity, entityIndex, seq, bytes, label);

    /// <summary>A block for a transport op at <paramref name="ticks"/>.</summary>
    public static TimelineMark Op(long ticks, TapDirection direction, int bytes) =>
        new(ticks, ticks, MarkKind.Block, direction, MarkFlags.Op, EntityKind.None, -1, -1, bytes, -1);

    /// <summary>A mark that is no frame: a lifetime, an open or close mark, or a split mark.</summary>
    public static TimelineMark Line(MarkKind kind, long start, long end, TapDirection direction = TapDirection.Inbound,
        EntityKind entity = EntityKind.None, int entityIndex = -1) =>
        new(start, Math.Max(start, end), kind, direction, MarkFlags.None, entity, entityIndex, -1, 0, -1);
}

/// <summary>A gap in the capture (Drop overflow): the hatched band.</summary>
/// <param name="Start">First dropped tick.</param>
/// <param name="End">Last dropped tick.</param>
/// <param name="Direction">The direction that lost data.</param>
/// <param name="DroppedOps">Transport ops dropped.</param>
/// <param name="DroppedBytes">Bytes those ops carried.</param>
public readonly record struct TimelineGap(long Start, long End, TapDirection Direction, long DroppedOps, long DroppedBytes);

/// <summary>An invocation before packing: <see cref="TimelineModel.PackInvocations"/> gives it a row.</summary>
/// <param name="Index">The invocation's index in the assembler.</param>
/// <param name="Start">The request's first tick (the capture's start when the request was not captured).</param>
/// <param name="End">The result's last tick, or the latest tick while it is pending.</param>
/// <param name="Label">Its text in <see cref="TimelineModel.GetLabel"/> ("#812 SendAttachment"); -1 for none.</param>
/// <param name="Flags">Cancelled, pending.</param>
/// <param name="Direction">The request's direction.</param>
public readonly record struct InvocationSpan(int Index, long Start, long End, int Label, MarkFlags Flags, TapDirection Direction);

/// <summary>
/// What is traced (timeline-lane README: the trace band spans the traced message; untraced items dim to 40 %): an entity
/// (an invocation, a pipe, a greeting) or, for a frame that belongs to none, that frame alone.
/// </summary>
/// <param name="IsActive">False when nothing is traced (no band, no dimming).</param>
/// <param name="Entity">The traced entity list; <see cref="EntityKind.None"/> for a lone frame.</param>
/// <param name="EntityIndex">Index into it.</param>
/// <param name="Direction">The lone frame's direction.</param>
/// <param name="Seq">The lone frame's sequence number.</param>
/// <param name="Start">The band's first tick.</param>
/// <param name="End">The band's last tick.</param>
public readonly record struct TimelineTrace(
    bool IsActive,
    EntityKind Entity,
    int EntityIndex,
    TapDirection Direction,
    long Seq,
    long Start,
    long End)
{
    /// <summary>Nothing traced.</summary>
    public static TimelineTrace None => new(false, EntityKind.None, -1, TapDirection.Inbound, -1, 0, 0);

    /// <summary>
    /// True when <paramref name="mark"/> belongs to the trace and stays at full opacity: transport ops by time (an op
    /// carries whatever frames it carries), everything else by entity or frame.
    /// </summary>
    public bool Lights(in TimelineMark mark)
    {
        if (!IsActive) return true;
        if ((mark.Flags & MarkFlags.Op) != 0) return mark.End >= Start && mark.Start <= End;
        if (Entity != EntityKind.None) return mark.Entity == Entity && mark.EntityIndex == EntityIndex;
        return mark.Seq == Seq && mark.Direction == Direction;
    }

    /// <summary>True when <paramref name="mark"/> is the traced invocation's span (drawn selected).</summary>
    public bool Selects(in TimelineMark mark) =>
        IsActive && mark.Kind == MarkKind.Span && mark.Entity == Entity && mark.EntityIndex == EntityIndex;
}
