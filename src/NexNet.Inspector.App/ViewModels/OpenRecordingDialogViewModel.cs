using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NexNet.Inspector.App.Controls.DataTable;
using NexNet.Inspector.App.Controls.DataTable.Sessions;
using NexNet.Inspector.App.Model;
using NexNet.Inspector.Core.Format;
using NexNet.Inspector.Core.Schema;
using NexNet.Inspector.Core.Session;

namespace NexNet.Inspector.App.ViewModels;

/// <summary>A run of a banner's detail text: the UI font, or mono for hashes, paths and commands.</summary>
/// <param name="Text">The text.</param>
/// <param name="Mono">True for the mono font.</param>
public readonly record struct TextPiece(string Text, bool Mono);

/// <summary>
/// The open-recording dialog (open-recording screen, 720 px), asked by <see cref="DocumentsViewModel.SessionPicker"/>:
/// <list type="bullet">
/// <item>the info strip: who recorded it and where, when and for how long, its size, sessions and gaps;</item>
/// <item>the session list (one radio per row), the first session picked;</item>
/// <item>a warning card when the file is damaged or was never closed (OQ3: every readable block still opens; the
/// index is rebuilt by <c>nexnet-tap repair</c>, which the card names);</item>
/// <item>for the picked session, a warning card when its embedded schema differs from the workspace schema, followed
/// by the choice Use the embedded schema / Use the workspace schema / Raw bytes only; or a note when it has no
/// readable schema (decoded raw, or by the workspace schema).</item>
/// </list>
/// A file that cannot be read at all shows the same dialog in its error state (the red status line and Close).
/// </summary>
public sealed partial class OpenRecordingDialogViewModel : ObservableObject
{
    /// <summary>A session row's height above its border (data-table.md: session lists 26).</summary>
    public const double RowHeight = 26;

    /// <summary>Rows shown before the list scrolls.</summary>
    public const int VisibleRows = 8;

    /// <summary>The list's header height above its border (data-table.md: 20).</summary>
    public const double HeaderHeight = 20;

    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    private readonly RecordingFile? _recording;
    private readonly NexusSchema? _workspace;
    private readonly string? _workspaceName;
    private readonly Action<OpenRecordingDialogViewModel> _close;
    private readonly Func<RecordingFile, Task<bool>>? _compare;
    private readonly TaskCompletionSource<RecordingOpenChoice?> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SessionListRow[] _rows;
    private bool _closed;

    /// <param name="recording">The opened recording; its opener owns and disposes it.</param>
    /// <param name="workspace">The workspace schema, if the user set one.</param>
    /// <param name="workspaceName">How to name the workspace schema (its path); its first export's name when null.</param>
    /// <param name="zone">The zone the recording's start is shown in.</param>
    /// <param name="close">Removes the dialog from the window; called once, on Open, Cancel or Compare.</param>
    /// <param name="compare">"Compare two sessions…" (P4 binds it); null disables the button.</param>
    public OpenRecordingDialogViewModel(RecordingFile recording, NexusSchema? workspace, string? workspaceName, TimeZoneInfo zone,
        Action<OpenRecordingDialogViewModel> close, Func<RecordingFile, Task<bool>>? compare = null)
    {
        ArgumentNullException.ThrowIfNull(recording);
        ArgumentNullException.ThrowIfNull(zone);
        ArgumentNullException.ThrowIfNull(close);
        _recording = recording;
        _workspace = workspace;
        _workspaceName = workspaceName;
        _close = close;
        _compare = compare;
        FileName = recording.Path is { } path ? System.IO.Path.GetFileName(path) : "recording";

        IReadOnlyList<NntlSessionOpen> sessions = recording.Sessions;
        RecordedSessionSummary[] summaries = new RecordedSessionSummary[sessions.Count];
        long first = long.MaxValue, last = long.MinValue, gaps = 0;
        int servers = 0;
        for (int i = 0; i < summaries.Length; i++)
        {
            RecordedSessionSummary summary = recording.Summarize(sessions[i]);
            summaries[i] = summary;
            first = Math.Min(first, summary.Open.Ticks);
            last = Math.Max(last, summary.LastTicks);
            gaps += summary.Gaps;
            if (summary.Open.Role == NntlSessionRole.Server) servers++;
        }

        NntlFileHeader header = recording.Header;
        long frequency = Math.Max(1, header.StopwatchFrequency);
        _rows = new SessionListRow[summaries.Length];
        for (int i = 0; i < summaries.Length; i++)
            _rows[i] = Row(recording, summaries[i], first, frequency);

        RecordedBy = servers == summaries.Length ? "server capture" : servers == 0 ? "client capture" : "capture";
        Host = string.IsNullOrEmpty(header.Host) ? null : header.Host;
        Producer = (header.Producer + " " + header.ProducerVersion).Trim();
        Started = summaries.Length == 0 ? "" : TimeZoneInfo.ConvertTime(new DateTimeOffset(header.ToUtc(first)), zone).ToString("yyyy-MM-dd HH:mm", Invariant);
        Span = summaries.Length == 0 ? "" : RecordingText.Span(last - first, frequency);
        Size = RecordingText.Size(recording.Length);
        SessionCount = HomeViewModel.SessionCount(summaries.Length);
        Gaps = RecordingText.Count(gaps);
        GapsNoun = gaps == 1 ? " gap" : " gaps";

        Source = new SessionListSource(_rows);
        (DamageTitle, DamageDetail) = Damage(recording, FileName);
        SelectedIndex = _rows.Length > 0 ? 0 : -1;
        OnSelectedIndexChanged(SelectedIndex);
    }

    private OpenRecordingDialogViewModel(string path, string reason, Action<OpenRecordingDialogViewModel> close)
    {
        _close = close;
        _rows = [];
        Source = new SessionListSource(_rows);
        FileName = System.IO.Path.GetFileName(path);
        FailureText = $"Could not read {FileName}: {reason}";
        DamageDetail = [];
        SelectedIndex = -1;
        RecordedBy = Producer = Started = Span = Size = SessionCount = Gaps = GapsNoun = "";
    }

    /// <summary>The dialog for a file that could not be read at all: its reason, and Close.</summary>
    /// <param name="path">The file.</param>
    /// <param name="reason">Why it could not be read.</param>
    /// <param name="close">Removes the dialog from the window.</param>
    public static OpenRecordingDialogViewModel Failed(string path, string reason, Action<OpenRecordingDialogViewModel> close)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(reason);
        ArgumentNullException.ThrowIfNull(close);
        return new OpenRecordingDialogViewModel(path, reason, close);
    }

    /// <summary>The picked session and schema; null when canceled (or Compare took over). Completes on close.</summary>
    public Task<RecordingOpenChoice?> Result => _result.Task;

    public string Title => "Open recording";

    /// <summary>The title row's mono subtitle: the file name.</summary>
    public string FileName { get; }

    /// <summary>"server capture", "client capture", or "capture" for a mix.</summary>
    public string RecordedBy { get; }

    /// <summary>The recording machine, when the file names it.</summary>
    public string? Host { get; }

    /// <summary>" on chat-prod-02", or nothing when the file does not name its machine.</summary>
    public string HostText => Host is null ? "" : " on " + Host;

    /// <summary>The writer and its version (the Recorded-by segment's tooltip).</summary>
    public string Producer { get; }

    /// <summary>When the first session's recording began: "2026-11-20 14:02".</summary>
    public string Started { get; }

    /// <summary>From the first session's start to the last block: "5 m 00 s".</summary>
    public string Span { get; }

    /// <summary>The file size: "41.2 MB".</summary>
    public string Size { get; }

    /// <summary>"4 sessions".</summary>
    public string SessionCount { get; }

    /// <summary>Gap blocks across the sessions (ops dropped in Drop mode), and their noun.</summary>
    public string Gaps { get; }

    public string GapsNoun { get; }

    /// <summary>The session list's rows.</summary>
    public IReadOnlyList<SessionListRow> Rows => _rows;

    /// <summary>The session list's columns.</summary>
    public IReadOnlyList<DataTableColumn> Columns => SessionListColumns.All;

    /// <summary>The session list (its rows draw their radios from its picked row).</summary>
    public SessionListSource Source { get; }

    /// <summary>The list's height (borders included): its header and up to <see cref="VisibleRows"/> rows, then it scrolls.</summary>
    public double TableHeight => HeaderHeight + 1 + (Math.Clamp(_rows.Length, 1, VisibleRows) * (RowHeight + 1)) + 1;

    /// <summary>The picked session's row; -1 for none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PrimaryLabel))]
    [NotifyCanExecuteChangedFor(nameof(OpenCommand))]
    public partial int SelectedIndex { get; set; }

    /// <summary>The primary button: "Open session 0x3A21".</summary>
    public string PrimaryLabel => (uint)SelectedIndex < (uint)_rows.Length ? "Open session " + _rows[SelectedIndex].Session : "Open session";

    /// <summary>True when the file could not be read: the dialog only says why.</summary>
    public bool IsFailed => FailureText is not null;

    /// <summary>Why the file could not be read; null when it was.</summary>
    public string? FailureText { get; }

    public string CancelLabel => IsFailed ? "Close" : "Cancel";

    /// <summary>The damaged-file card's title; null when the file is whole.</summary>
    public string? DamageTitle { get; }

    /// <summary>The damaged-file card's text.</summary>
    public IReadOnlyList<TextPiece> DamageDetail { get; }

    public bool HasDamage => DamageTitle is not null;

    /// <summary>What is known about the picked session's embedded schema.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsSchemaChoice), nameof(HasSchemaBanner), nameof(HasBody))]
    public partial SchemaStatus SchemaStatus { get; private set; }

    /// <summary>The schema card's title; null when the session's schema is embedded and matches.</summary>
    [ObservableProperty]
    public partial string? SchemaTitle { get; private set; }

    /// <summary>The schema card's text: the recording's and the workspace's schema and hash.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<TextPiece> SchemaDetail { get; private set; } = [];

    /// <summary>True when the schema card shows.</summary>
    public bool HasSchemaBanner => SchemaTitle is not null;

    /// <summary>True when the schema choice shows: the embedded schema differs from the workspace one.</summary>
    public bool ShowsSchemaChoice => SchemaStatus == SchemaStatus.Mismatch;

    /// <summary>True when anything shows below the session list.</summary>
    public bool HasBody => IsFailed || HasDamage || HasSchemaBanner;

    /// <summary>Which schema decodes the session, when the choice shows.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UsesEmbedded), nameof(UsesWorkspace), nameof(UsesRaw))]
    public partial SchemaChoice Choice { get; set; }

    /// <summary>The "Use the embedded schema" radio row.</summary>
    public bool UsesEmbedded
    {
        get => Choice == SchemaChoice.Embedded;
        set
        {
            if (value) Choice = SchemaChoice.Embedded;
        }
    }

    /// <summary>The "Use the workspace schema" radio row.</summary>
    public bool UsesWorkspace
    {
        get => Choice == SchemaChoice.Workspace;
        set
        {
            if (value) Choice = SchemaChoice.Workspace;
        }
    }

    /// <summary>The "Raw bytes only" radio row.</summary>
    public bool UsesRaw
    {
        get => Choice == SchemaChoice.Raw;
        set
        {
            if (value) Choice = SchemaChoice.Raw;
        }
    }

    /// <summary>Closed: opened, canceled, or handed to Compare.</summary>
    public bool IsClosed => _closed;

    /// <summary>The primary button, Enter, and a double-click on a row: open the picked session.</summary>
    [RelayCommand(CanExecute = nameof(CanOpen))]
    private void Open()
    {
        if (!CanOpen()) return;
        // The choice only applies while it shows; otherwise the resolution picks (the workspace schema or raw, OQ3).
        SchemaChoice schema = ShowsSchemaChoice ? Choice : SchemaChoice.Embedded;
        Finish(new RecordingOpenChoice(_rows[SelectedIndex].SessionId, schema));
    }

    private bool CanOpen() => !_closed && (uint)SelectedIndex < (uint)_rows.Length;

    /// <summary>Cancel, ✕ and Esc.</summary>
    [RelayCommand]
    private void Cancel() => Finish(null);

    /// <summary>"Compare two sessions…": hands the recording to Compare (P4), which opens what it compares by path.</summary>
    [RelayCommand(CanExecute = nameof(CanCompare))]
    private async Task Compare()
    {
        if (!CanCompare()) return;
        if (await _compare!(_recording!).ConfigureAwait(true)) Finish(null);
    }

    private bool CanCompare() => !_closed && _compare is not null && _rows.Length >= 2;

    partial void OnSelectedIndexChanged(int value)
    {
        Source.Picked = value;
        UpdateSchema();
    }

    private void Finish(RecordingOpenChoice? choice)
    {
        if (_closed) return;
        _closed = true;
        _result.TrySetResult(choice);
        OpenCommand.NotifyCanExecuteChanged();
        CompareCommand.NotifyCanExecuteChanged();
        _close(this);
    }

    private void UpdateSchema()
    {
        if (_recording is not { } recording || (uint)SelectedIndex >= (uint)_rows.Length)
        {
            SchemaStatus = SchemaStatus.Embedded;
            SchemaTitle = null;
            SchemaDetail = [];
            return;
        }

        NntlSessionOpen open = recording.Sessions[SelectedIndex];
        SchemaResolution resolution = recording.ResolveSchema(open, _workspace, SchemaChoice.Embedded);
        string decodes = _workspace is not null
            ? "Bodies decode with the workspace schema; unknown method ids show as raw."
            : "Bodies show as raw bytes; framing is still decoded.";
        (SchemaTitle, SchemaDetail) = resolution.Status switch
        {
            SchemaStatus.Mismatch when resolution.Embedded is { } embedded && _workspace is { } workspace =>
                ("The embedded schema differs from your workspace schema", MismatchDetail(embedded, workspace, _workspaceName)),
            SchemaStatus.Unreadable =>
                ("The recording's schema could not be read", (IReadOnlyList<TextPiece>)
                [
                    new("Session " + _rows[SelectedIndex].Session + " names schema ", false),
                    new(SchemaHash.TryRead(open.SchemaHash.Span, out ulong named) ? RecordingText.Hash(named) : "(malformed hash)", true),
                    new(", which is missing from the file or does not read. " + decodes, false),
                ]),
            SchemaStatus.Missing => ("The recording has no schema", [new(decodes, false)]),
            _ => (null, []),
        };
        SchemaStatus = resolution.Status;
    }

    /// <summary>"Recording: Chat · IR v1 · export hash 0x… Workspace: …\Chat.nnschema · hash 0x… (2 methods added)."</summary>
    public static IReadOnlyList<TextPiece> MismatchDetail(NexusSchema recorded, NexusSchema workspace, string? workspaceName)
    {
        ArgumentNullException.ThrowIfNull(recorded);
        ArgumentNullException.ThrowIfNull(workspace);
        return
        [
            new("Recording: " + Name(recorded) + " · IR v" + recorded.IrVersion.ToString(Invariant) + " · export hash ", false),
            new(RecordingText.Hash(recorded.Hash), true),
            new(". Workspace: ", false),
            new(workspaceName ?? Name(workspace), true),
            new(" · hash ", false),
            new(RecordingText.Hash(workspace.Hash), true),
            new(" (" + MethodChange(recorded, workspace) + ").", false),
        ];
    }

    /// <summary>How the workspace's methods differ from the recording's: "2 methods added", "1 method removed".</summary>
    public static string MethodChange(NexusSchema recorded, NexusSchema workspace)
    {
        ArgumentNullException.ThrowIfNull(recorded);
        ArgumentNullException.ThrowIfNull(workspace);
        HashSet<(bool Server, ushort Id)> before = MethodIds(recorded), after = MethodIds(workspace);
        int added = 0, removed = 0;
        foreach ((bool, ushort) id in after)
        {
            if (!before.Contains(id)) added++;
        }

        foreach ((bool, ushort) id in before)
        {
            if (!after.Contains(id)) removed++;
        }

        return (added, removed) switch
        {
            (0, 0) => "same methods, different signatures",
            (_, 0) => RecordingText.Plural(added, "method") + " added",
            (0, _) => RecordingText.Plural(removed, "method") + " removed",
            _ => RecordingText.Plural(added, "method") + " added, " + RecordingText.Count(removed) + " removed",
        };
    }

    private static HashSet<(bool Server, ushort Id)> MethodIds(NexusSchema schema)
    {
        HashSet<(bool, ushort)> ids = [];
        foreach (SchemaExport export in schema.Exports)
        {
            foreach (SchemaMethod method in export.ServerApi.Methods) ids.Add((true, method.Id));
            foreach (SchemaMethod method in export.ClientApi.Methods) ids.Add((false, method.Id));
        }

        return ids;
    }

    private static string Name(NexusSchema schema) => schema.Exports.Count > 0 ? schema.Exports[0].Name : "schema";

    private static SessionListRow Row(RecordingFile recording, in RecordedSessionSummary summary, long first, long frequency)
    {
        NntlSessionOpen open = summary.Open;
        string address = open.RemoteEndpoint ?? open.LocalEndpoint ?? "unknown address";
        // The nexus names the row's service; a session whose close block never came is said to have been cut off.
        string? about = !summary.HasClose
            ? "recording ended first"
            : recording.GetEmbeddedSchema(open) is { Exports.Count: > 0 } schema ? schema.Exports[0].Name : open.EndpointName;
        return new SessionListRow(
            open.SessionId,
            LiveDocumentViewModel.SessionHex(open.SessionId),
            string.IsNullOrEmpty(about) ? address : address + " · " + about,
            open.TransportName.ToUpperInvariant(),
            RecordingText.Offset(open.Ticks - first, frequency),
            RecordingText.Length(summary.DurationTicks, frequency),
            RecordingText.Count(summary.Ops));
    }

    private static (string? Title, IReadOnlyList<TextPiece> Detail) Damage(RecordingFile recording, string fileName)
    {
        RecordingWarnings warnings = recording.Warnings;
        if (warnings == RecordingWarnings.None) return (null, []);

        List<TextPiece> detail = [];
        if ((warnings & RecordingWarnings.IndexFromScan) != 0)
            detail.Add(new("Its index footer is missing, so the file was scanned (a recording that stopped mid-write). ", false));
        if ((warnings & RecordingWarnings.Damaged) != 0)
        {
            long bytes = 0;
            foreach (NntlDamage damage in recording.Damage) bytes += damage.Length;
            int regions = recording.Damage.Count;
            detail.Add(new(RecordingText.Plural(regions, "damaged region") + " (" + RecordingText.Plural(bytes, "byte") + ") "
                           + (regions == 1 ? "was" : "were") + " skipped. ", false));
        }

        detail.Add(new("Every readable block opens. To rebuild the index, run ", false));
        detail.Add(new("nexnet-tap repair " + fileName, true));
        detail.Add(new(".", false));
        string title = (warnings & RecordingWarnings.Damaged) != 0
            ? "Parts of this recording could not be read"
            : "This recording was not closed";
        return (title, detail);
    }
}
