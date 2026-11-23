using CommunityToolkit.Mvvm.ComponentModel;
using NexNet.Inspector.App.Controls.Hex;
using NexNet.Inspector.Core.Compare;

namespace NexNet.Inspector.App.ViewModels;

/// <summary>
/// The Compare "Hex · A ⇄ B" pane (PKG compare, bottom right): the bytes of the selected field diff's value in both
/// documents, interleaved row by row (<see cref="CompareHexModel"/>), with the selected field ringed on both sides.
/// </summary>
public sealed partial class CompareHexViewModel : ObservableObject
{
    /// <summary>The pane header's title.</summary>
    public const string HeaderTitle = "Hex · A ⇄ B";

    /// <summary>The bytes shown: one instance for the pane's life.</summary>
    public CompareHexModel Model { get; } = new();

    /// <summary>The pane header's title.</summary>
    public string Title => HeaderTitle;

    /// <summary>The pane header's subtitle ("header + body start").</summary>
    [ObservableProperty]
    public partial string Subtitle { get; private set; } = "";

    /// <summary>True when there are bytes to show.</summary>
    [ObservableProperty]
    public partial bool HasBytes { get; private set; }

    /// <summary>Shows a field diff's value, ringing <paramref name="row"/>; a null diff clears.</summary>
    /// <param name="compare">The compare the diff came from.</param>
    /// <param name="diff">The entity's field diff.</param>
    /// <param name="part">The value to show when no row is given; -1 picks the first that differs.</param>
    /// <param name="row">The field to ring; -1 for none.</param>
    public void Show(CompareSession? compare, EntityFieldDiff? diff, int part = -1, int row = -1)
    {
        if (compare is null || diff is null)
            Model.Clear();
        else
            Model.Load(compare, diff, part, row);
        HasBytes = Model.HasBytes;
        Subtitle = Model.HasBytes ? Model.Subtitle : "";
    }
}
