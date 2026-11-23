using NexNet.Inspector.App.Controls.PropertyTree;
using NexNet.Inspector.App.Model;
using NexNet.Inspector.Core.Compare;
using NexNet.Inspector.Core.Schema;

namespace NexNet.Inspector.App.Controls.DataTable.FieldDiff;

/// <summary>What a row of the field diff table is.</summary>
public enum FieldDiffEntryKind : byte
{
    /// <summary>A compared value's heading ("Arguments", "Item 3 · client → server").</summary>
    Part = 0,

    /// <summary>One <see cref="Core.Compare.FieldDiff"/> row.</summary>
    Field = 1,
}

/// <summary>One row of the table: a part heading or a field.</summary>
/// <param name="Kind">Heading or field.</param>
/// <param name="Part">Index into <see cref="EntityFieldDiff.Parts"/>.</param>
/// <param name="Row">For a field, index into <see cref="EntityFieldDiff.Rows"/>; -1 for a heading.</param>
public readonly record struct FieldDiffEntry(FieldDiffEntryKind Kind, int Part, int Row);

/// <summary>What one realized row shows, formatted for it alone.</summary>
/// <param name="Kind">Heading or field.</param>
/// <param name="Depth">Nesting: 0 for a heading, 1 + the field's depth for a field.</param>
/// <param name="Name">The heading, or the field's own name.</param>
/// <param name="Path">The field's full path (its tooltip); the heading again for a heading.</param>
/// <param name="A">A's value text; null when A has none.</param>
/// <param name="B">B's value text; null when B has none.</param>
/// <param name="Diff">How the field compares; a heading is Changed when any of its fields differ.</param>
public readonly record struct FieldDiffCells(FieldDiffEntryKind Kind, int Depth, string Name, string Path, string? A, string? B, FieldDiffKind Diff);

/// <summary>
/// The field diff's rows (data-table field-diff variant) over one entity's <see cref="EntityFieldDiff"/>: each part's
/// heading followed by its fields in pre-order. Holds only the small entry list; a value's text is formatted when its
/// row is realized, so a pipe compared item by item costs nothing for the items scrolled past.
/// </summary>
/// <remarks>UI thread only.</remarks>
public sealed class FieldDiffTableSource : DataTableSource
{
    private FieldDiffEntry[] _entries = [];
    private int _count;
    private long _version;

    public override int Count => _count;

    /// <summary>The diff shown; null when none.</summary>
    public EntityFieldDiff? Diff { get; private set; }

    /// <summary>Increments whenever <see cref="Set"/> replaces the rows (realized visuals re-format then).</summary>
    public long Version => _version;

    /// <summary>The row at <paramref name="index"/>.</summary>
    public FieldDiffEntry this[int index] => (uint)index < (uint)_count ? _entries[index] : throw new ArgumentOutOfRangeException(nameof(index));

    /// <summary>Shows a diff's rows; null shows none.</summary>
    public void Set(EntityFieldDiff? diff)
    {
        Diff = diff;
        _version++;
        _count = 0;
        if (diff is not null)
        {
            ReadOnlySpan<FieldDiffPart> parts = diff.Parts;
            int needed = parts.Length + diff.Rows.Length;
            if (_entries.Length < needed)
                _entries = new FieldDiffEntry[needed];
            for (int p = 0; p < parts.Length; p++)
            {
                _entries[_count++] = new FieldDiffEntry(FieldDiffEntryKind.Part, p, -1);
                for (int r = 0; r < parts[p].RowCount; r++)
                    _entries[_count++] = new FieldDiffEntry(FieldDiffEntryKind.Field, p, parts[p].FirstRow + r);
            }
        }

        NotifyReset();
    }

    /// <summary>The table row of a <see cref="EntityFieldDiff.Rows"/> row (each part's heading comes before its rows).</summary>
    /// <returns>The index, or -1 when no diff holds that row.</returns>
    public int IndexOfRow(int row)
    {
        if (Diff is not { } diff || (uint)row >= (uint)diff.Rows.Length)
            return -1;
        return row + diff.Rows[row].Part + 1;
    }

    /// <summary>True when the row at <paramref name="index"/> is a field that differs.</summary>
    public bool Differs(int index)
    {
        if ((uint)index >= (uint)_count || Diff is not { } diff)
            return false;
        FieldDiffEntry entry = _entries[index];
        return entry.Kind == FieldDiffEntryKind.Field && diff.Rows[entry.Row].Kind != FieldDiffKind.Same;
    }

    /// <summary>Formats the row at <paramref name="index"/> (allocates: realized rows only).</summary>
    public FieldDiffCells Format(int index)
    {
        EntityFieldDiff diff = Diff ?? throw new InvalidOperationException("no diff is shown");
        FieldDiffEntry entry = this[index];
        FieldDiffPart part = diff.Parts[entry.Part];
        if (entry.Kind == FieldDiffEntryKind.Part)
        {
            string heading = FieldDiffText.PartLabel(part);
            return new FieldDiffCells(FieldDiffEntryKind.Part, 0, heading, heading, part.A is null ? null : "", part.B is null ? null : "",
                part.Differing > 0 ? FieldDiffKind.Changed : FieldDiffKind.Same);
        }

        Core.Compare.FieldDiff row = diff.Rows[entry.Row];
        return new FieldDiffCells(FieldDiffEntryKind.Field, row.Depth + 1, FieldDiffText.FieldName(row.Path), row.Path,
            Value(part.A, row.FieldA), Value(part.B, row.FieldB), row.Kind);
    }

    public override DataTableRowVisual CreateVisual() => new FieldDiffRowVisual();

    public override void Prepare(DataTableRowVisual visual, int index)
    {
        if (visual is not FieldDiffRowVisual row || (uint)index >= (uint)_count)
            return;
        if (!row.Shows(_version, index))
            row.Set(_version, index, Format(index));
    }

    private static string? Value(DecodedValue? value, int field) =>
        value is null || (uint)field >= (uint)value.Fields.Length ? null : PropertyTreeModel.Format(value, value.Fields[field]);
}
