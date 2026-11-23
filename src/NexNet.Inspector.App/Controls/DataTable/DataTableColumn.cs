namespace NexNet.Inspector.App.Controls.DataTable;

/// <summary>How a column places its text (and drawn content) within its slot.</summary>
public enum DataTableAlignment : byte
{
    /// <summary>From the slot's left edge.</summary>
    Left = 0,

    /// <summary>Against the slot's right edge (numbers, byte counts, flags).</summary>
    Right = 1,

    /// <summary>Centred in the slot.</summary>
    Center = 2,
}

/// <summary>What a <see cref="DataTableControl"/> shows below its header.</summary>
public enum DataTableState : byte
{
    /// <summary>Rows (and, while loading, skeleton bars past the last one).</summary>
    Rows = 0,

    /// <summary>No rows: the centred tertiary hint (OQ2).</summary>
    Empty = 1,

    /// <summary>No rows yet and more are coming (a file still indexing): skeleton bars (OQ2).</summary>
    Loading = 2,
}

/// <summary>
/// One column of a <see cref="DataTableControl"/>: its header label, its width (fixed pixels, or a share of what the
/// fixed columns leave) and how its content aligns. The table is generic over its variants (Events, field diff, session
/// list): they differ only in their columns and in what their rows draw into each slot.
/// </summary>
/// <param name="Header">The header label; empty for a column without one (the Events direction glyph).</param>
/// <param name="Width">Pixels for a fixed column; the star weight for a <paramref name="IsStar"/> column.</param>
/// <param name="IsStar">True when the column shares the width the fixed columns leave, by weight.</param>
/// <param name="Alignment">How the header and the cells align.</param>
public readonly record struct DataTableColumn(string Header, double Width, bool IsStar, DataTableAlignment Alignment)
{
    /// <summary>A column of <paramref name="width"/> pixels.</summary>
    public static DataTableColumn Fixed(string header, double width, DataTableAlignment alignment = DataTableAlignment.Left) =>
        new(header, width, false, alignment);

    /// <summary>A column that takes <paramref name="weight"/> shares of the width the fixed columns leave.</summary>
    public static DataTableColumn Star(string header, double weight = 1, DataTableAlignment alignment = DataTableAlignment.Left) =>
        new(header, weight, true, alignment);
}

/// <summary>Where one column sits in a row: its left edge and width, in the row's coordinates.</summary>
/// <param name="X">The slot's left edge.</param>
/// <param name="Width">The slot's width (0 when the row is too narrow for a star column).</param>
public readonly record struct DataTableColumnSlot(double X, double Width)
{
    /// <summary>The slot's right edge.</summary>
    public double Right => X + Width;
}

/// <summary>Lays columns out across a row, like the package's CSS grid (<c>50px 74px 14px minmax(0,1fr) …</c>).</summary>
public static class DataTableColumnLayout
{
    /// <summary>
    /// Fills <paramref name="slots"/> with each column's slot across a row <paramref name="width"/> wide, inside
    /// <paramref name="left"/> and <paramref name="right"/> padding. Fixed columns keep their width; star columns share
    /// what is left by weight and shrink to 0 rather than push the fixed columns (minmax(0,1fr)).
    /// </summary>
    /// <param name="columns">The columns.</param>
    /// <param name="width">The row's width.</param>
    /// <param name="left">Padding before the first column.</param>
    /// <param name="right">Padding after the last (room for the scroll rail).</param>
    /// <param name="slots">Receives one slot per column; at least as long as <paramref name="columns"/>.</param>
    public static void Arrange(ReadOnlySpan<DataTableColumn> columns, double width, double left, double right, Span<DataTableColumnSlot> slots)
    {
        if (slots.Length < columns.Length)
            throw new ArgumentException("one slot per column is needed", nameof(slots));

        double fixedWidth = 0, weights = 0;
        foreach (ref readonly DataTableColumn column in columns)
        {
            if (column.IsStar) weights += Math.Max(0, column.Width);
            else fixedWidth += Math.Max(0, column.Width);
        }

        double free = Math.Max(0, width - left - right - fixedWidth);
        double x = left;
        for (int i = 0; i < columns.Length; i++)
        {
            DataTableColumn column = columns[i];
            double w = column.IsStar
                ? weights > 0 ? free * Math.Max(0, column.Width) / weights : 0
                : Math.Max(0, column.Width);
            slots[i] = new DataTableColumnSlot(x, w);
            x += w;
        }
    }
}
