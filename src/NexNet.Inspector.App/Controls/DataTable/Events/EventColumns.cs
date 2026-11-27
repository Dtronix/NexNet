namespace NexNet.Inspector.App.Controls.DataTable.Events;

/// <summary>
/// The Events table's columns (data-table.md): Seq 50 · Time ms 74 · direction 14 · Type (the rest) · Target 104 ·
/// Bytes 52 right · Flags 34 right.
/// </summary>
public static class EventColumns
{
    /// <summary>Column indexes, in order.</summary>
    public const int Seq = 0, Time = 1, Direction = 2, Type = 3, Target = 4, Bytes = 5, Flags = 6;

    /// <summary>The columns.</summary>
    public static IReadOnlyList<DataTableColumn> All { get; } =
    [
        DataTableColumn.Fixed("Seq", 50),
        DataTableColumn.Fixed("Time ms", 74),
        DataTableColumn.Fixed("", 14),
        DataTableColumn.Star("Type"),
        DataTableColumn.Fixed("Target", 104),
        DataTableColumn.Fixed("Bytes", 52, DataTableAlignment.Right),
        DataTableColumn.Fixed("Flags", 34, DataTableAlignment.Right),
    ];
}
