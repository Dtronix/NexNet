using CommunityToolkit.Mvvm.ComponentModel;
using NexNet.Inspector.App.Controls.DataTable;
using NexNet.Inspector.App.Controls.DataTable.FieldDiff;
using NexNet.Inspector.App.Model;
using NexNet.Inspector.Core.Compare;

namespace NexNet.Inspector.App.ViewModels;

/// <summary>
/// The Compare Field diff pane (PKG compare, bottom left; data-table field-diff variant): one entity's decoded values
/// compared field by field (I30's <see cref="EntityFieldDiff"/>), as rows Field | file A | file B, differing rows tinted;
/// the titled pane header names the entity and counts its differences. The selected row is the field the A ⇄ B hex view
/// rings (<see cref="SelectedRow"/>).
/// </summary>
public sealed partial class FieldDiffViewModel : ObservableObject
{
    /// <summary>The empty hint with no entity picked.</summary>
    public const string NoEntityText = "Pick an entity in the sidebar or a row of the timeline to compare its fields";

    /// <summary>The empty hint for an entity without decoded values (control frames, the preamble).</summary>
    public const string NoFieldsText = "Nothing decoded to compare · the hex view shows the bytes";

    private string _nameA = "A";
    private string _nameB = "B";
    private bool _showing;
    private string? _entityTitle;

    /// <summary>The rows: one instance for the pane's life.</summary>
    public FieldDiffTableSource Rows { get; } = new();

    /// <summary>The columns, headed by the two documents' names.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<DataTableColumn> Columns { get; private set; } = FieldDiffColumns.For("A", "B");

    /// <summary>The compare shown; null when none.</summary>
    public CompareSession? Compare { get; private set; }

    /// <summary>The entity shown (index in <see cref="CompareSession.Matches"/>); -1 when none.</summary>
    public int Match { get; private set; } = -1;

    /// <summary>The pane header's title.</summary>
    public string Title => "Field diff";

    /// <summary>The pane header's subtitle: the entity ("Invocation #812 SendAttachment"), and the part of the selected row.</summary>
    [ObservableProperty]
    public partial string Subtitle { get; private set; } = FieldDiffText.NoEntity;

    /// <summary>The count at the right of the header ("3 differences"); empty with no entity.</summary>
    [ObservableProperty]
    public partial string DifferenceText { get; private set; } = "";

    /// <summary>True when the entity's fields differ somewhere (the count is then drawn in split.nexnet).</summary>
    [ObservableProperty]
    public partial bool HasDifferences { get; private set; }

    /// <summary>The hint shown when there are no rows.</summary>
    [ObservableProperty]
    public partial string EmptyText { get; private set; } = NoEntityText;

    /// <summary>The selected table row; -1 for none. The table binds it two-way.</summary>
    [ObservableProperty]
    public partial int SelectedIndex { get; set; } = -1;

    /// <summary>The selected field (index into <see cref="EntityFieldDiff.Rows"/>); -1 for none or a part heading.</summary>
    public int SelectedRow =>
        Rows.Diff is not null && (uint)SelectedIndex < (uint)Rows.Count && Rows[SelectedIndex].Kind == FieldDiffEntryKind.Field
            ? Rows[SelectedIndex].Row
            : -1;

    /// <summary>The part of the selected row (a heading's own part); -1 for none.</summary>
    public int SelectedPart => Rows.Diff is not null && (uint)SelectedIndex < (uint)Rows.Count ? Rows[SelectedIndex].Part : -1;

    /// <summary>Raised when the selected row changes (the hex view follows).</summary>
    public event Action? SelectionChanged;

    /// <summary>Names the documents in the column headers (their tab names).</summary>
    public void SetFiles(string nameA, string nameB)
    {
        ArgumentNullException.ThrowIfNull(nameA);
        ArgumentNullException.ThrowIfNull(nameB);
        if (nameA == _nameA && nameB == _nameB)
            return;
        _nameA = nameA;
        _nameB = nameB;
        Columns = FieldDiffColumns.For(nameA, nameB);
    }

    /// <summary>Shows an entity's field diff; a null compare or a negative match clears.</summary>
    /// <param name="compare">The compare.</param>
    /// <param name="match">Index in <see cref="CompareSession.Matches"/>.</param>
    /// <param name="entityTitle">
    /// What the subtitle calls the entity (the Compare workspace's own title for it, so the panel and the rows agree);
    /// null names it from the compare (<see cref="FieldDiffText.Describe"/>).
    /// </param>
    public void Show(CompareSession? compare, int match, string? entityTitle = null) =>
        Show(compare, match, entityTitle, compare is not null && (uint)match < (uint)compare.Matches.Length ? compare.GetFieldDiff(match) : null);

    /// <summary>Shows an entity's field diff built elsewhere (off the UI thread); a null compare or diff clears.</summary>
    /// <param name="compare">The compare.</param>
    /// <param name="match">Index in <see cref="CompareSession.Matches"/>.</param>
    /// <param name="entityTitle">What the subtitle calls the entity; null names it from the compare.</param>
    /// <param name="diff">The entity's <see cref="CompareSession.GetFieldDiff"/>.</param>
    public void Show(CompareSession? compare, int match, string? entityTitle, EntityFieldDiff? diff)
    {
        _entityTitle = string.IsNullOrEmpty(entityTitle) ? null : entityTitle;
        if (compare is null || diff is null || (uint)match >= (uint)compare.Matches.Length)
        {
            Compare = null;
            Match = -1;
            Rows.Set(null);
            DifferenceText = "";
            HasDifferences = false;
            EmptyText = NoEntityText;
            _showing = true;
            try
            {
                SelectedIndex = -1;
            }
            finally
            {
                _showing = false;
            }

            Subtitle = FieldDiffText.NoEntity;
            SelectionChanged?.Invoke();
            return;
        }

        Compare = compare;
        Match = match;
        Rows.Set(diff);
        DifferenceText = FieldDiffText.Differences(diff.DifferenceCount);
        HasDifferences = diff.DifferenceCount > 0;
        EmptyText = NoFieldsText;
        _showing = true;
        try
        {
            SelectedIndex = -1;
        }
        finally
        {
            _showing = false;
        }

        UpdateSubtitle();
        SelectionChanged?.Invoke();
    }

    /// <summary>Selects a field (index into <see cref="EntityFieldDiff.Rows"/>), as a click in the hex view does.</summary>
    public void SelectRow(int row)
    {
        int index = Rows.IndexOfRow(row);
        if (index >= 0)
            SelectedIndex = index;
    }

    partial void OnSelectedIndexChanged(int value)
    {
        // Show raises once for the new entity, after its rows are in.
        if (_showing)
            return;
        UpdateSubtitle();
        SelectionChanged?.Invoke();
    }

    private void UpdateSubtitle()
    {
        if (Compare is not { } compare || Match < 0)
        {
            Subtitle = FieldDiffText.NoEntity;
            return;
        }

        string entity = _entityTitle ?? FieldDiffText.Describe(compare, Match);
        int part = SelectedPart;
        Subtitle = part >= 0 && Rows.Diff is { } diff ? entity + " · " + FieldDiffText.PartLabel(diff.Parts[part]) : entity;
    }
}
