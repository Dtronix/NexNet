using CommunityToolkit.Mvvm.ComponentModel;
using NexNet.Inspector.App.Services.Commands;
using NexNet.Inspector.Core.Assembly;
using NexNet.Inspector.Core.Session;

namespace NexNet.Inspector.App.Model.Palette;

/// <summary>The palette's result groups, in display order (OQ14: entities, frames, commands; then sessions and time).</summary>
public enum PaletteGroup : byte
{
    /// <summary>Invocations, pipes, channels, collections, the handshake.</summary>
    Entities = 0,

    /// <summary>Frames.</summary>
    Frames = 1,

    /// <summary>Commands.</summary>
    Commands = 2,

    /// <summary>Open documents.</summary>
    Sessions = 3,

    /// <summary>The <c>:</c> time jump.</summary>
    Time = 4,
}

/// <summary>What choosing a palette item does.</summary>
public enum PaletteTargetKind : byte
{
    /// <summary>Nothing (a group heading, a hint).</summary>
    None = 0,

    /// <summary>Selects (and so traces) an entity.</summary>
    Entity = 1,

    /// <summary>Selects a frame.</summary>
    Frame = 2,

    /// <summary>Runs a command.</summary>
    Command = 3,

    /// <summary>Activates a document.</summary>
    Session = 4,

    /// <summary>Moves the playhead and window to a time.</summary>
    Time = 5,
}

/// <summary>The icon column of an item (palette: ◆ trace, ▼/▲ direction colours).</summary>
public enum PaletteGlyph : byte
{
    /// <summary>None.</summary>
    None = 0,

    /// <summary>◆ an invocation (accent.trace when selected).</summary>
    Invocation,

    /// <summary>⇄ a pipe or channel.</summary>
    Pipe,

    /// <summary>≡ a collection.</summary>
    Collection,

    /// <summary>⇋ the handshake.</summary>
    Handshake,

    /// <summary>▼ an inbound frame.</summary>
    Inbound,

    /// <summary>▲ an outbound frame.</summary>
    Outbound,

    /// <summary>● a live document.</summary>
    Live,

    /// <summary>▤ a recording.</summary>
    Recording,

    /// <summary>A command's own glyph (<see cref="PaletteRow.Command"/>).</summary>
    Command,
}

/// <summary>Where a palette item leads.</summary>
/// <param name="Kind">What choosing it does.</param>
/// <param name="Entity">The entity list, for <see cref="PaletteTargetKind.Entity"/>.</param>
/// <param name="Index">The entity's index, or the document's position in the open documents.</param>
/// <param name="Frame">The frame, for <see cref="PaletteTargetKind.Frame"/>.</param>
/// <param name="Start">Where it starts (ticks); the time of a time jump.</param>
/// <param name="End">Where it ends (ticks).</param>
/// <param name="Command">The command, for <see cref="PaletteTargetKind.Command"/>.</param>
public readonly record struct PaletteTarget(
    PaletteTargetKind Kind,
    EntityKind Entity,
    int Index,
    FrameKey Frame,
    long Start,
    long End,
    AppCommand Command)
{
    /// <summary>No target.</summary>
    public static PaletteTarget None => new(PaletteTargetKind.None, EntityKind.None, -1, default, 0, 0, AppCommand.None);

    /// <summary>An entity spanning <paramref name="start"/> to <paramref name="end"/>.</summary>
    public static PaletteTarget OfEntity(EntityKind entity, int index, long start, long end) =>
        None with { Kind = PaletteTargetKind.Entity, Entity = entity, Index = index, Start = start, End = end };

    /// <summary>A frame starting at <paramref name="start"/>.</summary>
    public static PaletteTarget OfFrame(FrameKey frame, long start) => None with { Kind = PaletteTargetKind.Frame, Frame = frame, Start = start, End = start };

    /// <summary>A command.</summary>
    public static PaletteTarget OfCommand(AppCommand command) => None with { Kind = PaletteTargetKind.Command, Command = command };

    /// <summary>The open document at <paramref name="index"/>.</summary>
    public static PaletteTarget OfSession(int index) => None with { Kind = PaletteTargetKind.Session, Index = index };

    /// <summary>A time (ticks).</summary>
    public static PaletteTarget OfTime(long ticks) => None with { Kind = PaletteTargetKind.Time, Start = ticks, End = ticks };
}

/// <summary>
/// One line of the palette's results: a group heading (ENTITIES, FRAMES, COMMANDS) or an item (icon, label, secondary
/// detail, mono shortcut). Made for the visible results only (a few dozen per query); the list is virtualized.
/// </summary>
public sealed partial class PaletteRow : ObservableObject
{
    private PaletteRow(PaletteGroup group, bool isHeader, string label, string? detail, string? shortcut, PaletteGlyph glyph,
        AppCommand command, bool isEnabled, PaletteTarget target)
    {
        Group = group;
        IsHeader = isHeader;
        Label = label;
        Detail = detail;
        Shortcut = shortcut;
        Glyph = glyph;
        Command = command;
        IsEnabled = isEnabled;
        Target = target;
    }

    /// <summary>A group heading.</summary>
    public static PaletteRow Header(PaletteGroup group) => new(group, true, HeadingOf(group), null, null, PaletteGlyph.None, AppCommand.None, false,
        PaletteTarget.None);

    /// <summary>An item.</summary>
    public static PaletteRow Item(PaletteGroup group, string label, string? detail, PaletteGlyph glyph, PaletteTarget target,
        string? shortcut = null, bool isEnabled = true) =>
        new(group, false, label, detail, shortcut, glyph, target.Command, isEnabled && target.Kind != PaletteTargetKind.None, target);

    /// <summary>The group it belongs to.</summary>
    public PaletteGroup Group { get; }

    /// <summary>True for a group heading: never selected or run.</summary>
    public bool IsHeader { get; }

    /// <summary>True for an item.</summary>
    public bool IsItem => !IsHeader;

    /// <summary>The heading (sentence case; drawn in capitals) or the item's label.</summary>
    public string Label { get; }

    /// <summary>Secondary text after the label; null for none.</summary>
    public string? Detail { get; }

    /// <summary>A command's shortcut; null for none.</summary>
    public string? Shortcut { get; }

    /// <summary>The icon column.</summary>
    public PaletteGlyph Glyph { get; }

    /// <summary>The command, for a command item.</summary>
    public AppCommand Command { get; }

    /// <summary>False for an item that cannot run now (drawn disabled, skipped by Enter).</summary>
    public bool IsEnabled { get; }

    /// <summary>Where it leads.</summary>
    public PaletteTarget Target { get; }

    /// <summary>The keyboard's item: selected fill and inbound ring.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShortcutText))]
    public partial bool IsSelected { get; set; }

    /// <summary>The right column: the shortcut, or "Enter" on the selected item that has none.</summary>
    public string? ShortcutText => Shortcut ?? (IsSelected && IsEnabled ? "Enter" : null);

    /// <summary>The icon column's character.</summary>
    public string GlyphText => Glyph switch
    {
        PaletteGlyph.Invocation => "◆",
        PaletteGlyph.Pipe => "⇄",
        PaletteGlyph.Collection => "≡",
        PaletteGlyph.Handshake => "⇋",
        PaletteGlyph.Inbound => "▼",
        PaletteGlyph.Outbound => "▲",
        PaletteGlyph.Live => "●",
        PaletteGlyph.Recording => "▤",
        PaletteGlyph.Command => CommandGlyphText(CommandRegistry.Get(Command).Glyph),
        _ => "",
    };

    /// <summary>True for an inbound frame's ▼ (direction.inbound).</summary>
    public bool IsInbound => Glyph == PaletteGlyph.Inbound;

    /// <summary>True for an outbound frame's ▲ (direction.outbound).</summary>
    public bool IsOutbound => Glyph == PaletteGlyph.Outbound;

    /// <summary>True for an invocation's ◆ (accent.trace).</summary>
    public bool IsTraceGlyph => Glyph == PaletteGlyph.Invocation;

    /// <summary>What assistive technology reads for the item.</summary>
    public string AccessibleName => Detail is null ? Label : Label + ", " + Detail;

    private static string HeadingOf(PaletteGroup group) => group switch
    {
        PaletteGroup.Entities => "Entities",
        PaletteGroup.Frames => "Frames",
        PaletteGroup.Commands => "Commands",
        PaletteGroup.Sessions => "Sessions",
        _ => "Time",
    };

    private static string CommandGlyphText(CommandGlyph glyph) => glyph switch
    {
        CommandGlyph.Palette => "⌕",
        CommandGlyph.Open => "▤",
        CommandGlyph.Save => "⤓",
        CommandGlyph.Connect => "⇌",
        CommandGlyph.Record => "●",
        CommandGlyph.GoTo => "⇥",
        CommandGlyph.Trace => "⌖",
        CommandGlyph.Panes => "◫",
        CommandGlyph.Documents => "⧉",
        CommandGlyph.Theme => "◐",
        CommandGlyph.Help => "?",
        _ => "",
    };
}
