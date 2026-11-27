using NexNet.Inspector.Tap;

namespace NexNet.Inspector.App.Settings;

/// <summary>
/// The per-user settings kept in <c>settings.json</c> (<see cref="SettingsPaths"/>). Treated as immutable: a change is a
/// <c>with</c> copy handed to <see cref="SettingsStore.Update"/>, so a half-applied edit is never saved. The properties
/// have setters, not <c>init</c>, because the JSON source generator passes init-only properties a file leaves out as
/// default values, losing the initializers below; with setters only the properties present are assigned.
/// </summary>
public sealed record AppSettings
{
    /// <summary>How many recent files and endpoints are kept; older ones fall off the end.</summary>
    public const int MaxRecent = 10;

    public AppTheme Theme { get; set; }

    /// <summary>Recently opened recordings, newest first, at most <see cref="MaxRecent"/>.</summary>
    public IReadOnlyList<RecentFile> RecentFiles { get; set; } = [];

    /// <summary>Recently attached inspector endpoints, newest first, at most <see cref="MaxRecent"/>.</summary>
    public IReadOnlyList<RecentEndpoint> RecentEndpoints { get; set; } = [];

    /// <summary>The main window's last normal bounds and state; null until the window has been closed once.</summary>
    public WindowPlacement? Window { get; set; }

    /// <summary>The Connect dialog's last values. The token is never stored.</summary>
    public ConnectDefaults Connect { get; set; } = new();

    /// <summary>The workspace splitters' sizes (timeline, stream panes, inspector, Compare's sidebar and detail panel).</summary>
    public Model.PaneSizes Panes { get; set; } = new();

    /// <summary>The most bookmarks one recording keeps (I44): adding past it is refused.</summary>
    public const int MaxBookmarksPerRecording = 50;

    /// <summary>
    /// The most bookmarks kept over all recordings: past it, the recordings used least recently lose theirs first (whole
    /// recordings, never the one just changed).
    /// </summary>
    public const int MaxBookmarks = 500;

    /// <summary>
    /// The bookmarks (D3), keyed by recording (<see cref="Model.RecordingKey"/>: the file's header block and the session),
    /// most recently used recording first. No recording carries more than <see cref="MaxBookmarksPerRecording"/>, and all
    /// together no more than <see cref="MaxBookmarks"/>.
    /// </summary>
    public IReadOnlyList<RecordingBookmarks> Bookmarks { get; set; } = [];

    /// <summary>The bookmarks kept for <paramref name="key"/>; empty when none.</summary>
    public IReadOnlyList<SavedBookmark> BookmarksFor(string key)
    {
        foreach (RecordingBookmarks entry in Bookmarks)
        {
            if (string.Equals(entry.Key, key, StringComparison.Ordinal)) return entry.Items ?? [];
        }

        return [];
    }

    /// <summary>
    /// These settings with <paramref name="items"/> as the bookmarks of <paramref name="key"/>, that recording moved
    /// first (none removes it), cut to <see cref="MaxBookmarksPerRecording"/>; older recordings that no longer fit
    /// under <see cref="MaxBookmarks"/> are dropped.
    /// </summary>
    public AppSettings WithBookmarks(string key, IReadOnlyList<SavedBookmark> items, DateTimeOffset at)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(items);
        List<RecordingBookmarks> list = new(Bookmarks.Count + 1);
        if (items.Count > 0)
            list.Add(new RecordingBookmarks(key, at, items.Count > MaxBookmarksPerRecording ? [.. items.Take(MaxBookmarksPerRecording)] : [.. items]));
        foreach (RecordingBookmarks entry in Bookmarks)
        {
            if (!string.Equals(entry.Key, key, StringComparison.Ordinal)) list.Add(entry);
        }

        return this with { Bookmarks = CapBookmarks(list) };
    }

    /// <summary>These settings with <paramref name="path"/> first in the recent files (moved there if already listed).</summary>
    public AppSettings WithRecentFile(string path, int sessions, DateTimeOffset at)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return this with { RecentFiles = Bounded(new RecentFile(path, sessions, at), RecentFiles, static (a, b) => SamePath(a.Path, b.Path)) };
    }

    /// <summary>These settings with <paramref name="endpoint"/> first in the recent endpoints (moved there if already listed).</summary>
    public AppSettings WithRecentEndpoint(string endpoint, InspectorTargetKind target, DateTimeOffset at)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        return this with
        {
            RecentEndpoints = Bounded(new RecentEndpoint(endpoint, target, at), RecentEndpoints,
                static (a, b) => a.Target == b.Target && string.Equals(a.Endpoint, b.Endpoint, StringComparison.OrdinalIgnoreCase)),
        };
    }

    /// <summary>
    /// A loaded file made safe to use: blank entries dropped, duplicates collapsed and each list cut to
    /// <see cref="MaxRecent"/>, since the file may have been edited by hand.
    /// </summary>
    public AppSettings Normalized()
    {
        return this with
        {
            RecentFiles = Clean(RecentFiles, static f => !string.IsNullOrWhiteSpace(f.Path), static (a, b) => SamePath(a.Path, b.Path)),
            RecentEndpoints = Clean(RecentEndpoints, static e => !string.IsNullOrWhiteSpace(e.Endpoint),
                static (a, b) => a.Target == b.Target && string.Equals(a.Endpoint, b.Endpoint, StringComparison.OrdinalIgnoreCase)),
            Window = Window is { } w && w.IsUsable ? w : null,
            Connect = Connect ?? ConnectDefaults.Default,
            Panes = (Panes ?? new Model.PaneSizes()).Normalized(),
            Bookmarks = CleanBookmarks(Bookmarks),
        };
    }

    /// <summary>Paths compare as the OS does: Windows file names are case-insensitive, Linux ones are not.</summary>
    private static bool SamePath(string a, string b) =>
        string.Equals(a, b, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    // A hand-edited or older file: blank keys and names, empty or reversed windows and duplicate recordings dropped,
    // scopes that cover no time forgotten, then the caps applied.
    private static RecordingBookmarks[] CleanBookmarks(IReadOnlyList<RecordingBookmarks>? entries)
    {
        if (entries is null) return [];
        List<RecordingBookmarks> list = new(entries.Count);
        foreach (RecordingBookmarks entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Key) || entry.Items is null) continue;
            bool duplicate = false;
            foreach (RecordingBookmarks kept in list) duplicate |= string.Equals(kept.Key, entry.Key, StringComparison.Ordinal);
            if (duplicate) continue;

            List<SavedBookmark> items = new(Math.Min(entry.Items.Count, MaxBookmarksPerRecording));
            foreach (SavedBookmark item in entry.Items)
            {
                if (items.Count == MaxBookmarksPerRecording) break;
                if (Model.ViewBookmark.CleanName(item.Name) is not { } name || item.End <= item.Start) continue;
                SavedScope? scope = item.Scope is { } s && s.End > s.Start ? s with { Label = s.Label ?? "" } : null;
                items.Add(item with { Name = name, Scope = scope });
            }

            if (items.Count > 0) list.Add(entry with { Items = [.. items] });
        }

        return CapBookmarks(list);
    }

    // Keeps whole recordings, most recently used first, while the total stays under MaxBookmarks.
    private static RecordingBookmarks[] CapBookmarks(List<RecordingBookmarks> list)
    {
        int total = 0;
        int keep = 0;
        while (keep < list.Count && total + list[keep].Items.Count <= MaxBookmarks)
        {
            total += list[keep].Items.Count;
            keep++;
        }

        return [.. list.Take(keep)];
    }

    private static T[] Bounded<T>(T first, IReadOnlyList<T> rest, Func<T, T, bool> same)
    {
        List<T> list = new(Math.Min(rest.Count + 1, MaxRecent)) { first };
        foreach (T item in rest)
        {
            if (list.Count == MaxRecent) break;
            if (!same(first, item)) list.Add(item);
        }

        return [.. list];
    }

    private static T[] Clean<T>(IReadOnlyList<T>? items, Func<T, bool> keep, Func<T, T, bool> same)
    {
        if (items is null) return [];
        List<T> list = new(Math.Min(items.Count, MaxRecent));
        foreach (T item in items)
        {
            if (list.Count == MaxRecent) break;
            if (!keep(item)) continue;
            bool duplicate = false;
            foreach (T kept in list)
            {
                if (same(kept, item))
                {
                    duplicate = true;
                    break;
                }
            }

            if (!duplicate) list.Add(item);
        }

        return [.. list];
    }
}

/// <summary>A recording opened recently.</summary>
public readonly record struct RecentFile(string Path, int Sessions, DateTimeOffset LastUsed);

/// <summary>An inspector endpoint attached to recently, and whether a server session or a client was inspected.</summary>
public readonly record struct RecentEndpoint(string Endpoint, InspectorTargetKind Target, DateTimeOffset LastUsed);

/// <summary>One recording's bookmarks in <c>settings.json</c>.</summary>
/// <param name="Key">The recording key (<see cref="Model.RecordingKey"/>).</param>
/// <param name="LastUsed">When they last changed: the total cap drops the least recently used recordings first.</param>
/// <param name="Items">The bookmarks, in the ★ list's order.</param>
public readonly record struct RecordingBookmarks(string Key, DateTimeOffset LastUsed, IReadOnlyList<SavedBookmark> Items);

/// <summary>A bookmark as <c>settings.json</c> keeps it (<see cref="Model.ViewBookmark"/>); times in capture ticks.</summary>
public readonly record struct SavedBookmark(string Name, long Start, long End, SavedScope? Scope, DateTimeOffset Created);

/// <summary>A bookmark's scope as <c>settings.json</c> keeps it.</summary>
public readonly record struct SavedScope(long Start, long End, string Label);

/// <summary>Whether the main window was maximized when it closed.</summary>
public enum SavedWindowState : byte
{
    Normal = 0,
    Maximized = 1,
}

/// <summary>The main window's normal (restored) bounds in screen pixels/DIPs, and its state.</summary>
public readonly record struct WindowPlacement(int X, int Y, double Width, double Height, SavedWindowState State)
{
    /// <summary>False for sizes no real window had (zero, negative, NaN): such a file entry is ignored.</summary>
    public bool IsUsable => double.IsFinite(Width) && double.IsFinite(Height) && Width > 0 && Height > 0;
}

/// <summary>
/// The Connect dialog's last values: endpoint, what was inspected, overflow policy and the snapshot choice. A class with
/// initializers and setters (see <see cref="AppSettings"/>) so a value the file leaves out keeps its default.
/// </summary>
public sealed record ConnectDefaults
{
    /// <summary>A fresh default each time: the type has setters, so a shared instance could be changed under its users.</summary>
    public static ConnectDefaults Default => new();

    public string? Endpoint { get; set; }

    public InspectorTargetKind Target { get; set; } = InspectorTargetKind.Session;

    public InspectorOverflow Overflow { get; set; } = InspectorOverflow.Drop;

    /// <summary>Request a state snapshot on attach.</summary>
    public bool Snapshot { get; set; } = true;
}
