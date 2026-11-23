using NexNet.Inspector.Core.Assembly;
using NexNet.Inspector.Core.Model;
using NexNet.Inspector.Core.Session;

namespace NexNet.Inspector.App.Model;

/// <summary>What a <see cref="Selection"/> names.</summary>
public enum SelectionKind : byte
{
    /// <summary>Nothing is selected.</summary>
    None = 0,

    /// <summary>One frame (an Events row, a Ladder row, a timeline mark).</summary>
    Frame = 1,

    /// <summary>One logical message: a frame, an assembled message or a channel item (the trace).</summary>
    Message = 2,

    /// <summary>An entity: an invocation, pipe or greeting (a timeline span).</summary>
    Entity = 3,

    /// <summary>A byte range of one direction's stream (a hex selection).</summary>
    ByteRange = 4,
}

/// <summary>
/// The one selection every view of the active document follows. A plain value: views compare it to decide whether to
/// redraw, and a document keeps its own while another tab is active.
/// </summary>
/// <param name="Kind">What is selected.</param>
/// <param name="Frame">The frame, for <see cref="SelectionKind.Frame"/>; also the frame the selection was made from.</param>
/// <param name="Message">The message, for <see cref="SelectionKind.Message"/>.</param>
/// <param name="Entity">The entity list, for <see cref="SelectionKind.Entity"/>.</param>
/// <param name="EntityIndex">Index into that list.</param>
/// <param name="Direction">The stream of <see cref="Bytes"/>.</param>
/// <param name="Bytes">The byte range, for <see cref="SelectionKind.ByteRange"/>.</param>
public readonly record struct Selection(
    SelectionKind Kind,
    FrameKey Frame,
    MessageRef Message,
    EntityKind Entity,
    int EntityIndex,
    TapDirection Direction,
    StreamRange Bytes)
{
    /// <summary>Nothing selected.</summary>
    public static Selection None => new(SelectionKind.None, new FrameKey(TapDirection.Inbound, -1), MessageRef.None,
        EntityKind.None, -1, TapDirection.Inbound, default);

    /// <summary>True for <see cref="None"/>.</summary>
    public bool IsNone => Kind == SelectionKind.None;

    /// <summary>Selects a frame.</summary>
    public static Selection OfFrame(FrameKey frame) => None with { Kind = SelectionKind.Frame, Frame = frame, Direction = frame.Direction };

    /// <summary>Selects a message, remembering the frame it was picked from.</summary>
    public static Selection OfMessage(MessageRef message, FrameKey from) =>
        None with { Kind = SelectionKind.Message, Message = message, Frame = from, Direction = message.Direction };

    /// <summary>Selects an entity.</summary>
    public static Selection OfEntity(EntityKind entity, int index) =>
        None with { Kind = SelectionKind.Entity, Entity = entity, EntityIndex = index };

    /// <summary>Selects a byte range of one direction.</summary>
    public static Selection OfBytes(TapDirection direction, StreamRange bytes) =>
        None with { Kind = SelectionKind.ByteRange, Direction = direction, Bytes = bytes };
}

/// <summary>
/// The decoded field under the pointer, so the hex view and the property tree can light up each other's half.
/// </summary>
/// <param name="Message">The message whose decoded value holds the field.</param>
/// <param name="FieldIndex">The field's index in that decoded value; -1 for none.</param>
/// <param name="Offset">The field's first byte in the message's payload coordinates.</param>
/// <param name="Length">The field's length in bytes.</param>
public readonly record struct FieldHover(MessageRef Message, int FieldIndex, long Offset, int Length)
{
    /// <summary>No field hovered.</summary>
    public static FieldHover None => new(MessageRef.None, -1, 0, 0);

    /// <summary>True for <see cref="None"/>.</summary>
    public bool IsNone => FieldIndex < 0;
}
