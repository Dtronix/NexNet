using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace NexNet.Inspector.App.Controls.PropertyTree;

/// <summary>
/// The property tree's list: a virtualized ListBox over a <see cref="PropertyRowList"/> (only realized rows exist and
/// render their values). It adds what a plain list lacks: the row under the pointer (<see cref="HoveredRow"/>, which
/// the inspector publishes as the hovered field), sections that collapse when clicked, and ▸ / ▾ on nested fields
/// (click the glyph, double-click the row, or Left / Right on the selected row). Sections never take the selection.
/// </summary>
public sealed class PropertyTreeView : ListBox
{
    /// <summary>The width left of a field's name where its ▸ / ▾ glyph sits.</summary>
    public const double GlyphWidth = 12;

    /// <summary>A field row's left padding (property-tree: 22 px; sections 10 px).</summary>
    public const double FieldPadding = 22;

    public static readonly StyledProperty<PropertyRow?> HoveredRowProperty =
        AvaloniaProperty.Register<PropertyTreeView, PropertyRow?>(nameof(HoveredRow), defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<PropertyRow?> LinkedRowProperty =
        AvaloniaProperty.Register<PropertyTreeView, PropertyRow?>(nameof(LinkedRow));

    static PropertyTreeView() =>
        LinkedRowProperty.Changed.AddClassHandler<PropertyTreeView>(static (view, e) => view.OnLinkedRowChanged(e));

    public PropertyTreeView() =>
        // Tunnelling: the item handles a press itself (selection), and a section's press must fold it instead.
        AddHandler(PointerPressedEvent, OnPressedTunnel, Avalonia.Interactivity.RoutingStrategies.Tunnel);

    /// <summary>
    /// The row whose bytes are hovered elsewhere (the hex view): drawn with the hover fill (class "linked") though the
    /// pointer is not over it.
    /// </summary>
    public PropertyRow? LinkedRow
    {
        get => GetValue(LinkedRowProperty);
        set => SetValue(LinkedRowProperty, value);
    }

    protected override Type StyleKeyOverride => typeof(PropertyTreeView);

    /// <summary>The row under the pointer; null when the pointer is elsewhere.</summary>
    public PropertyRow? HoveredRow
    {
        get => GetValue(HoveredRowProperty);
        set => SetValue(HoveredRowProperty, value);
    }

    protected override void PrepareContainerForItemOverride(Control container, object? item, int index)
    {
        base.PrepareContainerForItemOverride(container, item, index);
        container.Classes.Set("section", item is PropertyRow { IsSection: true });
        container.Classes.Set("linked", item is PropertyRow row && ReferenceEquals(row, LinkedRow));
    }

    private void OnLinkedRowChanged(AvaloniaPropertyChangedEventArgs e)
    {
        if (e.OldValue is PropertyRow old && ContainerFromItem(old) is { } was) was.Classes.Set("linked", false);
        if (e.NewValue is PropertyRow row && ContainerFromItem(row) is { } now) now.Classes.Set("linked", true);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        HoveredRow = RowAt(e.Source);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        HoveredRow = null;
    }

    private void OnPressedTunnel(object? sender, PointerPressedEventArgs e)
    {
        if (RowAt(e.Source) is { } row && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && ContainerFromItem(row) is { } container)
        {
            if (row.IsSection)
            {
                row.Model.Toggle(row.Node);
                e.Handled = true;
                return;
            }

            // The glyph sits in the padding just left of the name.
            double x = e.GetPosition(container).X;
            double glyphLeft = FieldPadding + row.Indent - GlyphWidth;
            if (row.HasChildren && x >= glyphLeft && x < glyphLeft + GlyphWidth)
            {
                row.Model.Toggle(row.Node);
                e.Handled = true;
            }
        }
    }

    protected override void OnDoubleTapped(TappedEventArgs e)
    {
        base.OnDoubleTapped(e);
        if (RowAt(e.Source) is { IsSection: false, HasChildren: true } row) row.Model.Toggle(row.Node);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        // Unmodified only: Alt+Left is the window's Back (the scope).
        if (SelectedItem is PropertyRow { HasChildren: true } row && e.Key is Key.Left or Key.Right && e.KeyModifiers == KeyModifiers.None)
        {
            row.Model.SetExpanded(row.Node, e.Key == Key.Right);
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    private static PropertyRow? RowAt(object? source) =>
        (source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext as PropertyRow;
}
