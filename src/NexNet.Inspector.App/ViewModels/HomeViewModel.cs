using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NexNet.Inspector.App.Settings;
using NexNet.Inspector.Core.Format;
using NexNet.Inspector.Tap;

namespace NexNet.Inspector.App.ViewModels;

/// <summary>A row of the home's Recent endpoints card: kind label, endpoint, relative date.</summary>
/// <param name="Source">The saved entry (reopens the Connect dialog with it).</param>
/// <param name="Kind">"Server" or "Client".</param>
/// <param name="When">"today", "yesterday", "Nov 18".</param>
public readonly record struct RecentEndpointRow(RecentEndpoint Source, string Kind, string When)
{
    public string Endpoint => Source.Endpoint;
}

/// <summary>A row of the home's Recent recordings card: file name, session count, relative date.</summary>
/// <param name="Source">The saved entry (opens the file).</param>
/// <param name="Name">The file name.</param>
/// <param name="Sessions">"1 session", "41 sessions".</param>
/// <param name="When">"today", "yesterday", "Nov 17".</param>
public readonly record struct RecentRecordingRow(RecentFile Source, string Name, string Sessions, string When)
{
    public string Path => Source.Path;
}

/// <summary>
/// The empty window (the connect screen behind its dialog): the two action cards, the two recent lists from the
/// settings, and dropping a <c>.nntl</c> anywhere on it. The window supplies what needs it: the Connect dialog, opening a
/// file, the file picker.
/// </summary>
public sealed partial class HomeViewModel : ObservableObject
{
    /// <summary>The extension a recording has; only such files are offered to the drop target.</summary>
    public const string RecordingExtension = ".nntl";

    private readonly Func<AppSettings> _settings;
    private readonly TimeProvider _time;
    private readonly Action<RecentEndpoint?> _connect;
    private readonly Func<string, Task> _open;

    /// <param name="settings">The current settings (recents); read again on <see cref="Refresh"/>.</param>
    /// <param name="time">The clock the relative dates count from.</param>
    /// <param name="connect">Opens the Connect dialog, prefilled from a recent endpoint when one is given.</param>
    /// <param name="open">Opens a recording (I18's file open).</param>
    public HomeViewModel(Func<AppSettings> settings, TimeProvider time, Action<RecentEndpoint?> connect, Func<string, Task> open)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(connect);
        ArgumentNullException.ThrowIfNull(open);
        _settings = settings;
        _time = time;
        _connect = connect;
        _open = open;
        Refresh();
    }

    public string Title => MainWindowViewModel.AppName;

    /// <summary>The hero's secondary line under the app name.</summary>
    public string Tagline => "See every frame, message and session on the wire.";

    [ObservableProperty]
    public partial IReadOnlyList<RecentEndpointRow> RecentEndpoints { get; private set; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<RecentRecordingRow> RecentRecordings { get; private set; } = [];

    /// <summary>The name of the recording being dragged over the window; null when none is (the drop card's active state).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDragOver), nameof(DropText))]
    public partial string? DragFileName { get; private set; }

    public bool IsDragOver => DragFileName is not null;

    public string? DropText => DragFileName is null ? null : "Drop to open " + DragFileName;

    /// <summary>Why the last open failed (the red status line under the cards); null when it did not.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOpenError))]
    public partial string? OpenError { get; private set; }

    public bool HasOpenError => OpenError is not null;

    /// <summary>The window's file picker (Ctrl+O); null where there is none (tests, headless).</summary>
    public Func<Task<string?>>? PickFile { get; set; }

    /// <summary>Reads the recents again (after a document closed, the lists may have grown).</summary>
    public void Refresh()
    {
        AppSettings settings = _settings();
        DateTimeOffset now = _time.GetLocalNow();
        RecentEndpointRow[] endpoints = new RecentEndpointRow[settings.RecentEndpoints.Count];
        for (int i = 0; i < endpoints.Length; i++)
        {
            RecentEndpoint e = settings.RecentEndpoints[i];
            endpoints[i] = new RecentEndpointRow(e, e.Target == InspectorTargetKind.Client ? "Client" : "Server", RelativeDate(e.LastUsed, now));
        }

        RecentRecordingRow[] files = new RecentRecordingRow[settings.RecentFiles.Count];
        for (int i = 0; i < files.Length; i++)
        {
            RecentFile f = settings.RecentFiles[i];
            files[i] = new RecentRecordingRow(f, System.IO.Path.GetFileName(f.Path), SessionCount(f.Sessions), RelativeDate(f.LastUsed, now));
        }

        RecentEndpoints = endpoints;
        RecentRecordings = files;
    }

    /// <summary>"today", "yesterday", "Nov 18" (and the year once it is not this one), in the local calendar.</summary>
    public static string RelativeDate(DateTimeOffset when, DateTimeOffset now)
    {
        DateOnly day = DateOnly.FromDateTime(when.ToOffset(now.Offset).DateTime);
        DateOnly today = DateOnly.FromDateTime(now.DateTime);
        if (day == today) return "today";
        if (day == today.AddDays(-1)) return "yesterday";
        return day.Year == today.Year
            ? day.ToString("MMM d", CultureInfo.InvariantCulture)
            : day.ToString("MMM d, yyyy", CultureInfo.InvariantCulture);
    }

    public static string SessionCount(int sessions) =>
        sessions == 1 ? "1 session" : sessions.ToString(CultureInfo.InvariantCulture) + " sessions";

    /// <summary>True for a path a drop may open.</summary>
    public static bool IsRecording(string? path) =>
        path is not null && path.EndsWith(RecordingExtension, StringComparison.OrdinalIgnoreCase);

    /// <summary>A file entered the window: the drop card turns active when it is a recording.</summary>
    /// <returns>True when the drop would be accepted.</returns>
    public bool DragEnter(IReadOnlyList<string> paths)
    {
        string? recording = FirstRecording(paths);
        DragFileName = recording is null ? null : System.IO.Path.GetFileName(recording);
        return recording is not null;
    }

    /// <summary>The drag left the window, or was dropped.</summary>
    public void DragLeave() => DragFileName = null;

    /// <summary>Opens the first recording among the dropped files.</summary>
    /// <returns>False when none of them is a recording.</returns>
    public async Task<bool> DropAsync(IReadOnlyList<string> paths)
    {
        DragFileName = null;
        if (FirstRecording(paths) is not { } recording) return false;
        await OpenAsync(recording).ConfigureAwait(true);
        return true;
    }

    /// <summary>The "Connect to a live session" card and Ctrl+K.</summary>
    [RelayCommand]
    private void Connect() => _connect(null);

    /// <summary>The "Open a recording" card and Ctrl+O: the file picker, then the file.</summary>
    [RelayCommand]
    private async Task OpenFile()
    {
        if (PickFile is not { } pick || await pick().ConfigureAwait(true) is not { } path) return;
        await OpenAsync(path).ConfigureAwait(true);
    }

    [RelayCommand]
    private void OpenRecentEndpoint(RecentEndpointRow row) => _connect(row.Source);

    [RelayCommand]
    private Task OpenRecentRecording(RecentRecordingRow row) => OpenAsync(row.Path);

    private async Task OpenAsync(string path)
    {
        OpenError = null;
        try
        {
            await _open(path).ConfigureAwait(true);
        }
        catch (Exception e) when (e is NntlFormatException or IOException or UnauthorizedAccessException)
        {
            // The window shows an unreadable file in the open-recording dialog and never throws here; a host without
            // one (a test, a custom open) still gets the reason under the cards.
            OpenError = $"Could not open {System.IO.Path.GetFileName(path)}: {e.Message}";
        }
    }

    private static string? FirstRecording(IReadOnlyList<string> paths)
    {
        foreach (string path in paths)
        {
            if (IsRecording(path)) return path;
        }

        return null;
    }
}
