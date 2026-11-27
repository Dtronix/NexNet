using Avalonia;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using NexNet.Inspector.App.Controls.Primitives;
using NexNet.Inspector.App.Model;
using NexNet.Inspector.Core.Compare;

namespace NexNet.Inspector.App.Controls.DataTable.FieldDiff;

/// <summary>
/// Draws one field diff row (data-table field-diff variant, compare screen): the field's name in the UI font 12,
/// indented by its depth (a part heading in semibold secondary), A's value in mono 11, B's value in mono 11 — in
/// split.nexnet semibold when the field differs — and "—" in tertiary for a side without the field. A differing field
/// is tinted with diff.band. Text is shaped once per row, width and theme.
/// </summary>
public sealed class FieldDiffRowVisual : DataTableRowVisual
{
    /// <summary>The field name size (typography body 12).</summary>
    public const double NameSize = 12;

    /// <summary>The value size (typography mono data 11).</summary>
    public const double ValueSize = 11;

    /// <summary>Indent per nesting level of the Field column.</summary>
    public const double Indent = 12;

    // Room between a cell's text and the next column, so an ellipsis never touches it.
    private const double CellGap = 8;

    private readonly TextLayout?[] _text = new TextLayout?[3];
    private readonly double[] _widths = [-1, -1, -1];
    private ThemePalette? _palette;
    private long _version = -1;

    /// <summary>What the row shows.</summary>
    public FieldDiffCells Cells { get; private set; }

    /// <summary>True when the row is a field whose sides differ.</summary>
    public bool Differs => Cells.Kind == FieldDiffEntryKind.Field && Cells.Diff != FieldDiffKind.Same;

    public override ThemeBrush? Band => Differs ? ThemeBrush.DiffBand : null;

    /// <summary>True when the row already shows row <paramref name="index"/> of the source's rows <paramref name="version"/>.</summary>
    public bool Shows(long version, int index) => version == _version && index == Index;

    /// <summary>Shows a formatted row.</summary>
    public void Set(long version, int index, in FieldDiffCells cells)
    {
        _version = version;
        Index = index;
        Cells = cells;
        Clear();
    }

    public override void Render(DrawingContext context, ThemePalette palette, ReadOnlySpan<DataTableColumnSlot> columns, Size size)
    {
        if (columns.Length <= FieldDiffColumns.B || Index < 0)
            return;
        if (!ReferenceEquals(palette, _palette))
        {
            Clear();
            _palette = palette;
        }

        FieldDiffCells cells = Cells;
        bool part = cells.Kind == FieldDiffEntryKind.Part;
        double indent = cells.Depth * Indent;
        DataTableColumnSlot field = columns[FieldDiffColumns.Field];
        DrawCell(context, 0, new DataTableColumnSlot(field.X + indent, Math.Max(0, field.Width - indent)), size.Height, cells.Name,
            new Typeface(palette.Ui, FontStyle.Normal, part ? FontWeight.SemiBold : FontWeight.Normal), NameSize,
            palette[part ? ThemeBrush.TextSecondary : ThemeBrush.TextPrimary]);

        Typeface mono = new(palette.Mono);
        DrawValue(context, palette, 1, columns[FieldDiffColumns.A], size.Height, cells.A, mono, palette[ThemeBrush.TextPrimary]);
        bool differs = Differs;
        DrawValue(context, palette, 2, columns[FieldDiffColumns.B], size.Height, cells.B,
            differs ? new Typeface(palette.Mono, FontStyle.Normal, FontWeight.SemiBold) : mono,
            palette[differs ? ThemeBrush.SplitNexnet : ThemeBrush.TextPrimary]);
    }

    public override string? ToolTipAt(int column)
    {
        if (Index < 0)
            return null;
        return column switch
        {
            FieldDiffColumns.Field => Cells.Path,
            FieldDiffColumns.A => Cells.A is { Length: > 0 } a ? a : null,
            FieldDiffColumns.B => Cells.B is { Length: > 0 } b ? b : null,
            _ => null,
        };
    }

    public override string? AccessibleName
    {
        get
        {
            if (Index < 0)
                return null;
            FieldDiffCells cells = Cells;
            if (cells.Kind == FieldDiffEntryKind.Part)
                return cells.Name;
            string state = cells.Diff switch
            {
                FieldDiffKind.Same => "same",
                FieldDiffKind.Added => "only in B",
                FieldDiffKind.Removed => "only in A",
                _ => "differs",
            };
            return $"{cells.Path}, {state}, A {cells.A ?? "absent"}, B {cells.B ?? "absent"}";
        }
    }

    private void DrawValue(DrawingContext context, ThemePalette palette, int slot, DataTableColumnSlot column, double height, string? text,
        Typeface face, IBrush ink)
    {
        // A part heading has no value of its own: nothing when the side holds it, "—" when it does not.
        if (text is null)
            DrawCell(context, slot, column, height, FieldDiffText.Absent, new Typeface(palette.Mono), ValueSize, palette[ThemeBrush.TextTertiary]);
        else if (text.Length > 0)
            DrawCell(context, slot, column, height, text, face, ValueSize, ink);
    }

    private void DrawCell(DrawingContext context, int slot, DataTableColumnSlot column, double height, string text, Typeface face, double size,
        IBrush ink)
    {
        if (column.Width <= 0)
            return;
        if (_text[slot] is null || _widths[slot] != column.Width)
        {
            _text[slot]?.Dispose();
            _text[slot] = new TextLayout(text, face, size, ink, textTrimming: TextTrimming.CharacterEllipsis,
                maxWidth: Math.Max(0, column.Width - CellGap), maxLines: 1);
            _widths[slot] = column.Width;
        }

        TextLayout layout = _text[slot]!;
        layout.Draw(context, new Point(column.X, Math.Round((height - layout.Height) / 2)));
    }

    private void Clear()
    {
        for (int i = 0; i < _text.Length; i++)
        {
            _text[i]?.Dispose();
            _text[i] = null;
            _widths[i] = -1;
        }
    }
}
