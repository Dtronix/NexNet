using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using NexNet.Inspector.App.Controls.DataTable;
using NexNet.Inspector.App.Controls.DataTable.Sessions;
using NexNet.Inspector.App.Controls.Primitives;

namespace NexNet.Inspector.App.Tests.Capture;

/// <summary>
/// The data-table session-list variant (open-recording): the package's four sessions 720 px wide, 26 px rows with a
/// radio each, the picked row selected with the inbound bar — default, hover (the second row) and keyboard focus.
/// </summary>
public static class SessionListGallery
{
    /// <summary>The section's height in the gallery.</summary>
    public const double Height = 3 * (TableHeight + 30);

    private const double TableHeight = 2 + 21 + (4 * 27);

    public static IReadOnlyList<SessionListRow> Rows { get; } =
    [
        new(0x3A21, "0x3A21", "192.168.1.40:51022 · ChatNexus", "TCP", "00:00.0", "5:00.0", "18,248"),
        new(0x3A22, "0x3A22", "10.0.4.88:40112 · ChatNexus", "TCP", "00:41.3", "4:18.7", "9,104"),
        new(0x3A27, "0x3A27", "10.0.4.90:52281 · ChatNexus", "TCP", "03:12.9", "1:47.1", "2,310"),
        new(0x3A29, "0x3A29", "10.0.4.91:52390 · recording ended first", "TCP", "04:51.0", "0:00.4", "6"),
    ];

    public static Control Build()
    {
        StackPanel states = new() { Spacing = 16 };
        states.Children.Add(Captioned("default", Table()));
        states.Children.Add(Captioned("hover", Table(hover: 1)));
        states.Children.Add(Captioned("focus", Table(focus: 0)));
        return states;
    }

    private static Control Table(int hover = -1, int focus = -1)
    {
        SessionListSource source = new(Rows) { Picked = 0 };
        DataTableControl table = new()
        {
            ItemsSource = source,
            Columns = SessionListColumns.All,
            SelectedIndex = 0,
            RowHeight = 26,
            HeaderHeight = 20,
            CellPadding = new Thickness(14, 0, 20, 0),
            SelectionBar = ThemeBrush.DirectionInbound,
            Width = 720,
            Height = TableHeight,
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        ControlGallery.Bind(table, DataTableControl.BorderBrushProperty, "Brush.Border.Default");
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
