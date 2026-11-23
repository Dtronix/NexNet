using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using NexNet.Inspector.App.Controls.DataTable;
using NexNet.Inspector.App.Controls.DataTable.Events;
using NexNet.Inspector.App.Tests.DataTable;

namespace NexNet.Inspector.App.Tests.Capture;

/// <summary>
/// The data-table gallery section (PKG controls/data-table): the Events variant 640 px wide as data-table.html draws
/// each state — default (traced chunk 1/5, selected chunk 2/5), hover (DuplexPipeWrite under the pointer), focus (the
/// selected row with the keyboard ring), empty (the tertiary hint) and loading (skeleton bars).
/// </summary>
public static class DataTableGallery
{
    /// <summary>The section's height in the gallery.</summary>
    public const double Height = 3 * (128 + 30);

    public static Control Build()
    {
        WrapPanel states = new() { ItemSpacing = 16, LineSpacing = 16 };
        states.Children.Add(Captioned("default", Table(EventRowArray.Sample(), selected: 3)));
        states.Children.Add(Captioned("hover", Table(EventRowArray.Sample(), selected: 3, hover: 1)));
        states.Children.Add(Captioned("focus", Table(EventRowArray.Sample(), selected: 3, focus: 3)));
        states.Children.Add(Captioned("empty", Table(new EventRowArray(), height: 2 + 21 + 24 + 16 + 24)));
        states.Children.Add(Captioned("loading", Table(new EventRowArray(), loading: true, height: 2 + 21 + 4 + (4 * 24))));
        return states;
    }

    private static Control Table(DataTableSource rows, int selected = -1, int hover = -1, int focus = -1, bool loading = false,
        double height = 2 + 21 + (5 * 21))
    {
        DataTableControl table = new()
        {
            ItemsSource = rows,
            Columns = EventColumns.All,
            SelectedIndex = selected,
            IsLoading = loading,
            EmptyText = "No frames in view · waiting for traffic",
            Width = 640,
            Height = height,
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        ControlGallery.Bind(table, DataTableControl.BorderBrushProperty, "Brush.Border.Default");

        // States the gallery cannot produce with input, applied once the rows exist.
        if (hover >= 0 || focus >= 0) table.LayoutUpdated += (_, _) => table.Preview(hover, focus);
        return table;
    }

    private static Control Captioned(string caption, Control content)
    {
        StackPanel cell = new() { Spacing = 6 };
        cell.Children.Add(content);
        cell.Children.Add(ControlGallery.Text(caption, "FontSize.Data", "Brush.Text.Secondary"));
        return cell;
    }
}
