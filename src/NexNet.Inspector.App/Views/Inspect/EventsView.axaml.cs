using Avalonia.Controls;
using NexNet.Inspector.App.Controls.DataTable;

namespace NexNet.Inspector.App.Views.Inspect;

/// <summary>The Events pane's body: an Events <see cref="DataTableControl"/> over an <c>EventsViewModel</c>.</summary>
public partial class EventsView : UserControl
{
    public EventsView() => InitializeComponent();

    /// <summary>The table.</summary>
    public DataTableControl Grid => Table;
}
