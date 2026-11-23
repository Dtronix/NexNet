using Avalonia.Controls;
using NexNet.Inspector.App.Controls.Hex;

namespace NexNet.Inspector.App.Views.Compare;

/// <summary>The Compare Hex · A ⇄ B pane over a <c>CompareHexViewModel</c>.</summary>
public partial class CompareHexPane : UserControl
{
    public CompareHexPane() => InitializeComponent();

    /// <summary>The hex view.</summary>
    public CompareHexView Hex => View;
}
