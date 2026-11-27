namespace NexNet.Inspector.App.Controls.DataTable.Sessions;

/// <summary>
/// The session list's rows over a fixed array (a recording's sessions do not change while its dialog is open). The
/// picked row is the table's selection; the source also knows it, because each row draws its own radio.
/// </summary>
/// <remarks>UI thread only.</remarks>
public sealed class SessionListSource : DataTableSource
{
    private readonly SessionListRow[] _rows;
    private int _picked = -1;

    /// <param name="rows">The rows, in file order.</param>
    public SessionListSource(IReadOnlyList<SessionListRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        _rows = [.. rows];
    }

    public override int Count => _rows.Length;

    /// <summary>The row whose radio is on; -1 for none.</summary>
    public int Picked
    {
        get => _picked;
        set
        {
            if (value == _picked) return;
            _picked = value;
            NotifyRowsChanged();
        }
    }

    /// <summary>The row at <paramref name="index"/>.</summary>
    public SessionListRow this[int index] => _rows[index];

    public override DataTableRowVisual CreateVisual() => new SessionRowVisual();

    public override void Prepare(DataTableRowVisual visual, int index)
    {
        if (visual is SessionRowVisual row && (uint)index < (uint)_rows.Length)
            row.Set(index, _rows[index], index == _picked);
    }
}
