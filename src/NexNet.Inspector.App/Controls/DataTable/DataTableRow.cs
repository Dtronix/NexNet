using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using NexNet.Inspector.App.Controls.Primitives;

namespace NexNet.Inspector.App.Controls.DataTable;

/// <summary>
/// One realized row of a <see cref="DataTableControl"/>: a drawn <see cref="ListBoxItem"/> (no template) that paints its
/// state background, the 1 px row border, the selection bar or keyboard focus ring, and its
/// <see cref="DataTableRowVisual"/>'s cells. The background comes from the theme (Themes/DataTable.axaml): trace tint
/// <c>hex.body-selected</c>, hover <c>surface.raised</c>, selected <c>state.selected</c>.
/// </summary>
public sealed class DataTableRow : ListBoxItem
{
    /// <summary>True when the row belongs to the traced message (styled to the trace tint).</summary>
    public static readonly StyledProperty<bool> IsTracedProperty =
        AvaloniaProperty.Register<DataTableRow, bool>(nameof(IsTraced));

    /// <summary>Set by the theme while the row has keyboard focus: the 1 px inbound ring replaces the selection bar.</summary>
    public static readonly StyledProperty<bool> ShowsFocusRingProperty =
        AvaloniaProperty.Register<DataTableRow, bool>(nameof(ShowsFocusRing));

    /// <summary>The selection bar's width (data-table.html: inset 3px 0 0 accent.trace).</summary>
    public const double SelectionBarWidth = 3;

    private int _tipColumn = -1;

    static DataTableRow()
    {
        AffectsRender<DataTableRow>(IsTracedProperty, ShowsFocusRingProperty, BackgroundProperty, IsSelectedProperty);
    }

    public bool IsTraced
    {
        get => GetValue(IsTracedProperty);
        set => SetValue(IsTracedProperty, value);
    }

    public bool ShowsFocusRing
    {
        get => GetValue(ShowsFocusRingProperty);
        set => SetValue(ShowsFocusRingProperty, value);
    }

    /// <summary>What the row draws; created by its table's source on first prepare and kept across recycling.</summary>
    public DataTableRowVisual? Visual { get; internal set; }

    internal DataTableControl? Table { get; set; }

    protected override Type StyleKeyOverride => typeof(DataTableRow);

    public override void Render(DrawingContext context)
    {
        ThemePalette palette = ThemeBrushes.For(this);
        Size size = Bounds.Size;
        double body = Math.Max(0, size.Height - 1);

        // The theme's background is transparent at rest, still filled so the whole row takes the pointer.
        if (Background is { } background) context.FillRectangle(background, new Rect(0, 0, size.Width, body));
        context.FillRectangle(palette[ThemeBrush.BorderRow], new Rect(0, body, size.Width, size.Height - body));
        if (IsSelected && !ShowsFocusRing)
            context.FillRectangle(palette[ThemeBrush.AccentTrace], new Rect(0, 0, SelectionBarWidth, body));

        if (Visual is { } visual && Table is { } table)
            visual.Render(context, palette, table.ColumnSlots, new Size(size.Width, body));

        if (ShowsFocusRing)
            context.DrawRectangle(null, palette.Pen(ThemeBrush.DirectionInbound, 1), new Rect(0.5, 0.5, Math.Max(0, size.Width - 1), Math.Max(0, size.Height - 1)));
    }

    /// <summary>Shows a state without input, for the control gallery and tests (hover and keyboard focus).</summary>
    internal void Preview(bool hover, bool focusRing)
    {
        PseudoClasses.Set(":pointerover", hover);
        PseudoClasses.Set(":focus-visible", focusRing);
    }

    /// <summary>The visual was prepared again: re-read its state and redraw.</summary>
    internal void Refresh()
    {
        IsTraced = Visual?.IsTraced ?? false;
        _tipColumn = -1;
        ToolTip.SetTip(this, null);
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (Visual is not { } visual || Table is not { } table) return;

        // The tooltip follows the column under the pointer (a cut target, a flag), so only that cell explains itself.
        double x = e.GetPosition(this).X;
        ReadOnlySpan<DataTableColumnSlot> slots = table.ColumnSlots;
        int column = -1;
        for (int i = 0; i < slots.Length; i++)
        {
            if (x >= slots[i].X && x < slots[i].Right)
            {
                column = i;
                break;
            }
        }

        if (column == _tipColumn) return;
        _tipColumn = column;
        ToolTip.SetTip(this, column >= 0 ? visual.ToolTipAt(column) : null);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ThemeVariantScope.ActualThemeVariantProperty)
            InvalidateVisual();
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new RowAutomationPeer(this);

    // A grid row, named by its cells (README: selected row announced with type + time).
    private sealed class RowAutomationPeer(DataTableRow owner) : ListItemAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.DataItem;

        protected override string? GetNameCore() => ((DataTableRow)Owner).Visual?.AccessibleName ?? base.GetNameCore();
    }
}
