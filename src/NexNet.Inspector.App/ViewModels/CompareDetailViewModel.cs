using NexNet.Inspector.Core.Compare;

namespace NexNet.Inspector.App.ViewModels;

/// <summary>
/// The Compare workspace's bottom panel (PKG compare: 200 px, Field diff with the hex pane at 42 % width): the picked
/// entity's field diff and its A ⇄ B bytes, linked both ways — a row picked in the table rings its bytes in both
/// documents, a byte clicked in the hex view selects its field's row.
/// </summary>
public sealed class CompareDetailViewModel
{
    public CompareDetailViewModel()
    {
        FieldDiff.SelectionChanged += OnFieldSelected;
    }

    /// <summary>The Field diff pane.</summary>
    public FieldDiffViewModel FieldDiff { get; } = new();

    /// <summary>The Hex · A ⇄ B pane.</summary>
    public CompareHexViewModel Hex { get; } = new();

    /// <summary>Names the documents (their tab names) in the field diff's column headers.</summary>
    public void SetFiles(string nameA, string nameB) => FieldDiff.SetFiles(nameA, nameB);

    /// <summary>Shows an entity of a compare; a null compare or a negative match clears both panes.</summary>
    /// <param name="compare">The compare.</param>
    /// <param name="match">Index in <see cref="CompareSession.Matches"/>.</param>
    /// <param name="entityTitle">The entity's title for the Field diff subtitle; null names it from the compare.</param>
    public void Show(CompareSession? compare, int match, string? entityTitle = null) => FieldDiff.Show(compare, match, entityTitle);

    /// <summary>Shows an entity with its field diff already built (off the UI thread); a null diff clears both panes.</summary>
    /// <param name="compare">The compare.</param>
    /// <param name="match">Index in <see cref="CompareSession.Matches"/>.</param>
    /// <param name="entityTitle">The entity's title for the Field diff subtitle; null names it from the compare.</param>
    /// <param name="diff">The entity's <see cref="CompareSession.GetFieldDiff"/>.</param>
    public void Show(CompareSession? compare, int match, string? entityTitle, EntityFieldDiff? diff) =>
        FieldDiff.Show(compare, match, entityTitle, diff);

    /// <summary>Selects the field at a hex byte's row (the hex view's click).</summary>
    public void PickField(int row) => FieldDiff.SelectRow(row);

    private void OnFieldSelected() =>
        Hex.Show(FieldDiff.Compare, FieldDiff.Rows.Diff, FieldDiff.SelectedPart, FieldDiff.SelectedRow);
}
