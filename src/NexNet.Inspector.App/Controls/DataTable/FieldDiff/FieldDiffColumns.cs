using NexNet.Inspector.App.Controls.Primitives;

namespace NexNet.Inspector.App.Controls.DataTable.FieldDiff;

/// <summary>
/// The field diff's columns (data-table.md field-diff variant): Field | file A | file B, sharing the width, each file
/// column headed by its file's colour square and name.
/// </summary>
public static class FieldDiffColumns
{
    /// <summary>Column indexes, in order.</summary>
    public const int Field = 0, A = 1, B = 2;

    /// <summary>File A's colour (the Compare file headers' blue).</summary>
    public const ThemeBrush ColourA = ThemeBrush.DirectionInbound;

    /// <summary>File B's colour (the Compare file headers' orange).</summary>
    public const ThemeBrush ColourB = ThemeBrush.DirectionOutbound;

    /// <summary>The columns, the file columns headed by the documents' names.</summary>
    /// <param name="nameA">Document A's tab name ("chat-repro.nntl").</param>
    /// <param name="nameB">Document B's tab name ("server-all.nntl › 0x3A21").</param>
    public static IReadOnlyList<DataTableColumn> For(string nameA, string nameB) =>
    [
        DataTableColumn.Star("Field", 0.9),
        DataTableColumn.Star(nameA) with { Swatch = ColourA },
        DataTableColumn.Star(nameB) with { Swatch = ColourB },
    ];
}
