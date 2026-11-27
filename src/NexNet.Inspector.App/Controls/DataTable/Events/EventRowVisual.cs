using Avalonia;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using NexNet.Inspector.App.Controls.Primitives;
using NexNet.Inspector.App.Model;
using NexNet.Inspector.Core.Assembly;
using NexNet.Inspector.Core.Framing;
using NexNet.Inspector.Core.Model;

namespace NexNet.Inspector.App.Controls.DataTable.Events;

/// <summary>
/// Draws one Events row (data-table.html <c>.r</c>): Seq mono 11 secondary, Time mono 11, the direction triangle
/// (▼ direction.inbound, ▲ direction.outbound), Type in the UI font 11.5 with an ellipsis, Target mono 11 secondary with
/// an ellipsis and a tooltip, Bytes mono 11 right-aligned, and the flags as chips (<see cref="FlagChip.Draw"/>, 2 px
/// apart) against the right edge. Text is shaped once per row and theme and reused while the row is only redrawn.
/// </summary>
public sealed class EventRowVisual : DataTableRowVisual
{
    /// <summary>The mono data size (typography mono 11).</summary>
    public const double DataSize = 11;

    /// <summary>The Type column's size (UI 11.5).</summary>
    public const double TypeSize = 11.5;

    /// <summary>Space between two flag chips.</summary>
    public const double ChipGap = 2;

    // The direction glyphs, 7 × 6, drawn at the column's left edge.
    private static readonly Geometry Down = Geometry.Parse("M0,0 L7,0 L3.5,6 Z");
    private static readonly Geometry Up = Geometry.Parse("M3.5,0 L7,6 L0,6 Z");

    private ThemePalette? _palette;
    private TextLayout? _seq;
    private TextLayout? _time;
    private TextLayout? _type;
    private TextLayout? _target;
    private TextLayout? _bytes;
    private string? _typeText;
    private string? _targetText;
    private double _typeWidth = -1;
    private double _targetWidth = -1;

    /// <summary>The row shown.</summary>
    public EventRow Row { get; private set; }

    /// <summary>Shows <paramref name="row"/> at <paramref name="index"/>; text is shaped again only when it changed.</summary>
    public void Set(int index, in EventRow row)
    {
        Index = index;
        IsTraced = row.IsTraced;
        // The trace tint is the row's background, not its text: only other data shapes again.
        bool sameText = (row with { IsTraced = Row.IsTraced }) == Row;
        Row = row;
        if (sameText && _typeText is not null) return;
        Clear();
    }

    public override void Render(DrawingContext context, ThemePalette palette, ReadOnlySpan<DataTableColumnSlot> columns, Size size)
    {
        if (columns.Length <= EventColumns.Flags || Index < 0) return;
        if (!ReferenceEquals(palette, _palette))
        {
            Clear();
            _palette = palette;
        }

        EventRow row = Row;
        Typeface mono = new(palette.Mono);
        _seq ??= Text(EventText.Number(row), mono, DataSize, palette[ThemeBrush.TextSecondary]);
        _time ??= Text(EventText.Time(row), mono, DataSize, palette[ThemeBrush.TextPrimary]);
        _bytes ??= Text(EventText.Bytes(row), mono, DataSize, palette[ThemeBrush.TextPrimary]);
        _typeText ??= EventText.Type(row);
        _targetText ??= EventText.Target(row);

        DataTableColumnSlot typeSlot = columns[EventColumns.Type];
        if (_type is null || _typeWidth != typeSlot.Width)
        {
            _type?.Dispose();
            // A channel or collection frame keeps room for its chip after the wire type.
            double chip = PipeChip.MeasureWidth(palette, EventText.PipeKind(row));
            double typeWidth = chip > 0 ? Math.Max(0, typeSlot.Width - chip - ChipGap - 2) : typeSlot.Width;
            _type = Text(_typeText, new Typeface(palette.Ui), TypeSize, palette[ThemeBrush.TextPrimary], typeWidth);
            _typeWidth = typeSlot.Width;
        }

        DataTableColumnSlot targetSlot = columns[EventColumns.Target];
        if (_target is null || _targetWidth != targetSlot.Width)
        {
            _target?.Dispose();
            _target = Text(_targetText, mono, DataSize, palette[ThemeBrush.TextSecondary], targetSlot.Width);
            _targetWidth = targetSlot.Width;
        }

        double height = size.Height;
        DrawLeft(context, _seq, columns[EventColumns.Seq], height);
        DrawLeft(context, _time, columns[EventColumns.Time], height);
        if (typeSlot.Width > 0)
        {
            DrawLeft(context, _type, typeSlot, height);
            DrawPipeChip(context, palette, row, typeSlot, height);
        }
        if (targetSlot.Width > 0 && _targetText.Length > 0) DrawLeft(context, _target, targetSlot, height);
        DataTableColumnSlot bytesSlot = columns[EventColumns.Bytes];
        _bytes.Draw(context, new Point(bytesSlot.Right - _bytes.WidthIncludingTrailingWhitespace, Middle(_bytes, height)));

        DataTableColumnSlot direction = columns[EventColumns.Direction];
        bool inbound = row.Direction == TapDirection.Inbound;
        using (context.PushTransform(Matrix.CreateTranslation(direction.X, Math.Round((height - 6) / 2))))
            context.DrawGeometry(palette[inbound ? ThemeBrush.DirectionInbound : ThemeBrush.DirectionOutbound], null, inbound ? Down : Up);

        DrawFlags(context, palette, row.Flags, columns[EventColumns.Flags], height);
    }

    public override string? ToolTipAt(int column) => column switch
    {
        EventColumns.Type => EventText.PipeKind(Row) is var kind && kind != PipeContent.Raw
            ? (_typeText ?? EventText.Type(Row)) + " · " + PipeText.Describe(kind)
            : _typeText ?? EventText.Type(Row),
        EventColumns.Target => (_targetText ?? EventText.Target(Row)) is { Length: > 0 } target ? target : null,
        EventColumns.Flags => DescribeFlags(Row.Flags),
        _ => null,
    };

    public override string? AccessibleName => Index < 0 ? null : EventText.Accessible(Row);

    // The CH / CO chip right after the type's text (the wire type stays; the chip says what rides on the pipe).
    private void DrawPipeChip(DrawingContext context, ThemePalette palette, in EventRow row, DataTableColumnSlot slot, double height)
    {
        PipeContent kind = EventText.PipeKind(row);
        if (kind == PipeContent.Raw || _type is null) return;
        double x = slot.X + Math.Ceiling(_type.WidthIncludingTrailingWhitespace) + ChipGap + 2;
        if (x + PipeChip.MeasureWidth(palette, kind) > slot.Right) return;
        PipeChip.Draw(context, palette, kind, new Point(x, Math.Round((height - FlagChip.ChipHeight) / 2)));
    }

    /// <summary>The width the row's flag chips take, gaps included.</summary>
    public static double FlagsWidth(ThemePalette palette, FrameFlags flags)
    {
        ArgumentNullException.ThrowIfNull(palette);
        double width = 0;
        foreach (FrameFlags flag in FlagChip.DisplayOrder)
        {
            if ((flags & flag) == 0) continue;
            if (width > 0) width += ChipGap;
            width += FlagChip.MeasureWidth(palette, flag);
        }

        return width;
    }

    private static void DrawFlags(DrawingContext context, ThemePalette palette, FrameFlags flags, DataTableColumnSlot slot, double height)
    {
        if (flags == FrameFlags.None) return;
        double x = slot.Right - FlagsWidth(palette, flags);
        double y = Math.Round((height - FlagChip.ChipHeight) / 2);
        foreach (FrameFlags flag in FlagChip.DisplayOrder)
        {
            if ((flags & flag) == 0) continue;
            x += FlagChip.Draw(context, palette, flag, new Point(x, y)) + ChipGap;
        }
    }

    private static string? DescribeFlags(FrameFlags flags)
    {
        if (flags == FrameFlags.None) return null;
        List<string> parts = [];
        foreach (FrameFlags flag in FlagChip.DisplayOrder)
        {
            if ((flags & flag) != 0) parts.Add(FlagChip.Description(flag));
        }

        return string.Join("\n", parts);
    }

    private static void DrawLeft(DrawingContext context, TextLayout text, DataTableColumnSlot slot, double height) =>
        text.Draw(context, new Point(slot.X, Middle(text, height)));

    private static double Middle(TextLayout text, double height) => Math.Round((height - text.Height) / 2);

    private static TextLayout Text(string text, Typeface face, double size, IBrush ink, double maxWidth = double.PositiveInfinity) =>
        new(text, face, size, ink, textTrimming: double.IsFinite(maxWidth) ? TextTrimming.CharacterEllipsis : TextTrimming.None,
            maxWidth: double.IsFinite(maxWidth) ? Math.Max(0, maxWidth) : double.PositiveInfinity, maxLines: 1);

    private void Clear()
    {
        _seq?.Dispose();
        _time?.Dispose();
        _type?.Dispose();
        _target?.Dispose();
        _bytes?.Dispose();
        _seq = _time = _type = _target = _bytes = null;
        _typeText = _targetText = null;
        _typeWidth = _targetWidth = -1;
    }
}
