using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using NexNet.Inspector.App.Controls.Primitives;

namespace NexNet.Inspector.App.Controls.DataTable;

/// <summary>
/// A <see cref="DataTableControl"/>'s header row (data-table.md): its column labels in 10.5 px text.secondary on the
/// table's slots, over a 1 px border.default bottom border. Drawn; the labels are shaped once per theme and width.
/// </summary>
public sealed class DataTableHeader : Control
{
    /// <summary>The label size (typography meta 10.5).</summary>
    public const double LabelSize = 10.5;

    private TextLayout?[] _labels = [];
    private double[] _widths = [];
    private ThemePalette? _palette;
    private IReadOnlyList<DataTableColumn>? _columns;

    internal DataTableControl? Table { get; set; }

    /// <summary>The header row's height above its border (data-table.md: 20 px).</summary>
    public double RowHeight { get; set; } = 20;

    protected override Size MeasureOverride(Size availableSize) => new(0, RowHeight + 1);

    public override void Render(DrawingContext context)
    {
        ThemePalette palette = ThemeBrushes.For(this);
        Size size = Bounds.Size;
        context.FillRectangle(palette[ThemeBrush.BorderDefault], new Rect(0, RowHeight, size.Width, 1));
        if (Table is not { } table || table.Columns is not { } columns) return;

        ReadOnlySpan<DataTableColumnSlot> slots = table.ColumnSlots;
        if (!ReferenceEquals(palette, _palette) || !ReferenceEquals(columns, _columns) || _labels.Length != columns.Count)
        {
            Clear();
            _palette = palette;
            _columns = columns;
            _labels = new TextLayout?[columns.Count];
            _widths = new double[columns.Count];
        }

        for (int i = 0; i < columns.Count && i < slots.Length; i++)
        {
            DataTableColumn column = columns[i];
            if (column.Header.Length == 0 || slots[i].Width <= 0) continue;
            if (_labels[i] is null || _widths[i] != slots[i].Width)
            {
                _labels[i]?.Dispose();
                _labels[i] = new TextLayout(column.Header, new Typeface(palette.Ui), LabelSize, palette[ThemeBrush.TextSecondary],
                    textTrimming: TextTrimming.CharacterEllipsis, maxWidth: slots[i].Width, maxLines: 1);
                _widths[i] = slots[i].Width;
            }

            TextLayout label = _labels[i]!;
            double x = column.Alignment switch
            {
                DataTableAlignment.Right => slots[i].Right - label.WidthIncludingTrailingWhitespace,
                DataTableAlignment.Center => slots[i].X + ((slots[i].Width - label.WidthIncludingTrailingWhitespace) / 2),
                _ => slots[i].X,
            };
            label.Draw(context, new Point(x, Math.Round((RowHeight - label.Height) / 2)));
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ThemeVariantScope.ActualThemeVariantProperty)
            InvalidateVisual();
    }

    private void Clear()
    {
        foreach (TextLayout? label in _labels) label?.Dispose();
        _labels = [];
    }
}
