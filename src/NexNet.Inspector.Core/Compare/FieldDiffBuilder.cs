using NexNet.Inspector.Core.Assembly;
using NexNet.Inspector.Core.Schema;

namespace NexNet.Inspector.Core.Compare;

/// <summary>
/// Compares decoded value trees field by field. Children pair by name (members, parameters), else by element index
/// (sequence elements, array positions), else by position; a field is <see cref="FieldDiffKind.Same"/> when its kind
/// and its bytes (headers and children included) are equal, so a container is Changed exactly when something inside
/// it is.
/// </summary>
internal sealed class FieldDiffBuilder
{
    private readonly List<FieldDiffPart> _parts = [];
    private readonly List<FieldDiff> _rows = [];

    public EntityFieldDiff Build(int match) => new(match, _parts, _rows);

    /// <summary>Adds one compared value and its rows.</summary>
    public void AddPart(FieldDiffPartKind kind, WireFlow flow, int ordinal, DecodedValue? a, DecodedValue? b)
    {
        var part = _parts.Count;
        var first = _rows.Count;
        var firstA = a is { Fields.Length: > 0 } ? 0 : -1;
        var firstB = b is { Fields.Length: > 0 } ? 0 : -1;
        DiffSiblings(part, a, firstA, b, firstB, 0, "");
        var differing = 0;
        for (var i = first; i < _rows.Count; i++)
        {
            if (_rows[i].Kind != FieldDiffKind.Same)
                differing++;
        }

        _parts.Add(new FieldDiffPart(kind, flow, ordinal, a, b, first, _rows.Count - first, differing));
    }

    private void DiffSiblings(int part, DecodedValue? a, int firstA, DecodedValue? b, int firstB, int depth, string prefix)
    {
        var childrenA = Siblings(a, firstA);
        var childrenB = Siblings(b, firstB);
        var used = new bool[childrenB.Count];
        var cursor = 0;
        for (var i = 0; i < childrenA.Count; i++)
        {
            var partner = FindPartner(a!, childrenA[i], i, b, childrenB, used, cursor);
            if (partner < 0)
            {
                Emit(part, a!, childrenA[i], i, CompareSide.A, depth, prefix);
                continue;
            }

            used[partner] = true;
            cursor = partner + 1;
            DiffPair(part, a!, childrenA[i], i, b!, childrenB[partner], depth, prefix);
        }

        for (var j = 0; j < childrenB.Count; j++)
        {
            if (!used[j])
                Emit(part, b!, childrenB[j], j, CompareSide.B, depth, prefix);
        }
    }

    private void DiffPair(int part, DecodedValue a, int fieldA, int position, DecodedValue b, int fieldB, int depth, string prefix)
    {
        ref readonly var fa = ref a.Fields[fieldA];
        ref readonly var fb = ref b.Fields[fieldB];
        var same = fa.Kind == fb.Kind && a.GetBytes(fa).SequenceEqual(b.GetBytes(fb));
        var path = Join(prefix, fa, position);
        _rows.Add(new FieldDiff(part, path, depth, same ? FieldDiffKind.Same : FieldDiffKind.Changed, fieldA, fieldB, a.GetRange(fa),
            b.GetRange(fb)));

        if (fa.Kind == fb.Kind)
        {
            DiffSiblings(part, a, fa.FirstChild, b, fb.FirstChild, depth + 1, path);
            return;
        }

        // Different kinds have unrelated children: A's go, B's come.
        DiffSiblings(part, a, fa.FirstChild, null, -1, depth + 1, path);
        DiffSiblings(part, null, -1, b, fb.FirstChild, depth + 1, path);
    }

    // A field (and everything under it) that only one side has.
    private void Emit(int part, DecodedValue value, int field, int position, CompareSide side, int depth, string prefix)
    {
        ref readonly var f = ref value.Fields[field];
        var path = Join(prefix, f, position);
        _rows.Add(side == CompareSide.A
            ? new FieldDiff(part, path, depth, FieldDiffKind.Removed, field, -1, value.GetRange(f), PayloadRange.None)
            : new FieldDiff(part, path, depth, FieldDiffKind.Added, -1, field, PayloadRange.None, value.GetRange(f)));
        var child = f.FirstChild;
        for (var i = 0; child >= 0; i++)
        {
            Emit(part, value, child, i, side, depth + 1, path);
            child = value.Fields[child].NextSibling;
        }
    }

    private static List<int> Siblings(DecodedValue? value, int first)
    {
        var list = new List<int>();
        if (value is null)
            return list;
        var fields = value.Fields;
        for (var i = first; i >= 0 && i < fields.Length; i = fields[i].NextSibling)
            list.Add(i);
        return list;
    }

    // Tries the next unused field of B first: the fields of both sides almost always come in the same order.
    private static int FindPartner(DecodedValue a, int fieldA, int position, DecodedValue? b, List<int> childrenB, bool[] used, int cursor)
    {
        if (b is null)
            return -1;
        ref readonly var fa = ref a.Fields[fieldA];
        if (cursor < childrenB.Count && !used[cursor] && SameKey(fa, position, b.Fields[childrenB[cursor]], cursor))
            return cursor;
        for (var j = 0; j < childrenB.Count; j++)
        {
            if (!used[j] && SameKey(fa, position, b.Fields[childrenB[j]], j))
                return j;
        }

        return -1;
    }

    private static bool SameKey(in DecodedField a, int positionA, in DecodedField b, int positionB)
    {
        if (a.Name is not null || b.Name is not null)
            return string.Equals(a.Name, b.Name, StringComparison.Ordinal) && a.ElementIndex == b.ElementIndex;
        if (a.ElementIndex >= 0 || b.ElementIndex >= 0)
            return a.ElementIndex == b.ElementIndex;
        return positionA == positionB;
    }

    private static string Join(string prefix, in DecodedField field, int position)
    {
        var segment = field.Name
                      ?? (field.ElementIndex >= 0 ? $"[{field.ElementIndex.ToString(System.Globalization.CultureInfo.InvariantCulture)}]"
                          : $"[{position.ToString(System.Globalization.CultureInfo.InvariantCulture)}]");
        if (prefix.Length == 0)
            return segment;
        return segment[0] == '[' ? prefix + segment : prefix + "." + segment;
    }
}

/// <summary>
/// The field diff of one union entity: each of its decoded values (arguments, result, channel items…) compared field by
/// field, as rows for the Field diff table and byte ranges for the A ⇄ B hex view. Built on demand for the selected
/// entity (<see cref="CompareSession.GetFieldDiff"/>), never for the whole union.
/// </summary>
public sealed class EntityFieldDiff
{
    private readonly List<FieldDiffPart> _parts;
    private readonly List<FieldDiff> _rows;

    internal EntityFieldDiff(int match, List<FieldDiffPart> parts, List<FieldDiff> rows)
    {
        Match = match;
        _parts = parts;
        _rows = rows;
        foreach (var part in parts)
            DifferenceCount += part.Differing;
    }

    /// <summary>The entity's index in <see cref="CompareSession.Matches"/>.</summary>
    public int Match { get; }

    /// <summary>The compared values, in order.</summary>
    public ReadOnlySpan<FieldDiffPart> Parts => System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_parts);

    /// <summary>Every row of every part, part after part.</summary>
    public ReadOnlySpan<FieldDiff> Rows => System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_rows);

    /// <summary>Rows that are not <see cref="FieldDiffKind.Same"/>.</summary>
    public int DifferenceCount { get; }

    /// <summary>The rows of one part.</summary>
    /// <param name="part">Index into <see cref="Parts"/>.</param>
    public ReadOnlySpan<FieldDiff> GetRows(int part)
    {
        var p = _parts[part];
        return Rows.Slice(p.FirstRow, p.RowCount);
    }

    /// <summary>The value a row's field belongs to on one side.</summary>
    public DecodedValue? GetValue(in FieldDiff row, CompareSide side) => side == CompareSide.A ? _parts[row.Part].A : _parts[row.Part].B;
}
