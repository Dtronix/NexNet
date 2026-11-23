namespace NexNet.Inspector.App.Controls.DataTable.Sessions;

/// <summary>
/// One row of the session list (data-table session-list variant, open-recording): the texts its cells show, made once
/// when the dialog opens (a recording holds a handful of sessions, read from its index alone).
/// </summary>
/// <param name="SessionId">The recorded session's id.</param>
/// <param name="Session">The id as tabs show it ("0x3A21").</param>
/// <param name="Remote">The remote address and the nexus ("192.168.1.40:51022 · ChatNexus").</param>
/// <param name="Transport">The transport ("TCP").</param>
/// <param name="Start">When the session's recording began, from the earliest one ("00:41.3").</param>
/// <param name="Duration">How long it was recorded ("4:18.7").</param>
/// <param name="Events">Transport ops recorded, with thousands separators.</param>
public readonly record struct SessionListRow(
    long SessionId,
    string Session,
    string Remote,
    string Transport,
    string Start,
    string Duration,
    string Events);

/// <summary>
/// The session list's columns (data-table.md session-list variant): radio 26 · Session 78 · Remote (the rest) ·
/// Transport 88 · Start 74 · Duration 70 · Events 70 right.
/// </summary>
public static class SessionListColumns
{
    /// <summary>Column indexes, in order.</summary>
    public const int Radio = 0, Session = 1, Remote = 2, Transport = 3, Start = 4, Duration = 5, Events = 6;

    /// <summary>The columns.</summary>
    public static IReadOnlyList<DataTableColumn> All { get; } =
    [
        DataTableColumn.Fixed("", 26),
        DataTableColumn.Fixed("Session", 78),
        DataTableColumn.Star("Remote"),
        DataTableColumn.Fixed("Transport", 88),
        DataTableColumn.Fixed("Start", 74),
        DataTableColumn.Fixed("Duration", 70),
        DataTableColumn.Fixed("Events", 70, DataTableAlignment.Right),
    ];
}
