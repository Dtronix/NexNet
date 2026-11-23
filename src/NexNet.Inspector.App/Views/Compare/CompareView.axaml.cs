using Avalonia.Controls;
using NexNet.Inspector.App.Controls.Compare;

namespace NexNet.Inspector.App.Views.Compare;

/// <summary>The Compare workspace over a <c>CompareWorkspaceViewModel</c>.</summary>
public partial class CompareView : UserControl
{
    public CompareView() => InitializeComponent();

    /// <summary>The drawn aligned rows.</summary>
    public CompareRowsControl RowsView => Rows;

    /// <summary>The Entities sidebar's list.</summary>
    public ListBox EntityTree => EntityList;
}
