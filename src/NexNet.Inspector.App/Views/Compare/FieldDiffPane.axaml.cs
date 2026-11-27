using Avalonia.Controls;
using NexNet.Inspector.App.Controls.DataTable;

namespace NexNet.Inspector.App.Views.Compare;

/// <summary>The Compare Field diff pane over a <c>FieldDiffViewModel</c>.</summary>
public partial class FieldDiffPane : UserControl
{
    public FieldDiffPane() => InitializeComponent();

    /// <summary>The table.</summary>
    public DataTableControl Grid => Table;

    /// <summary>The difference count in the header.</summary>
    public TextBlock CountText => Count;
}
