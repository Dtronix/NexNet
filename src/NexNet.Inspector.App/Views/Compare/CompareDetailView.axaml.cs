using Avalonia.Controls;
using NexNet.Inspector.App.ViewModels;

namespace NexNet.Inspector.App.Views.Compare;

/// <summary>The Compare bottom panel: the Field diff and Hex · A ⇄ B panes over a <see cref="CompareDetailViewModel"/>.</summary>
public partial class CompareDetailView : UserControl
{
    public CompareDetailView()
    {
        InitializeComponent();
        HexView.Hex.FieldPicked += row => (DataContext as CompareDetailViewModel)?.PickField(row);
    }

    /// <summary>The Field diff pane.</summary>
    public FieldDiffPane FieldDiff => FieldDiffView;

    /// <summary>The hex pane.</summary>
    public CompareHexPane Hex => HexView;
}