using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NexNet.Inspector.App.Model;
using NexNet.Inspector.App.Services;
using NexNet.Inspector.Core.Format;
using NexNet.Inspector.Tap;

namespace NexNet.Inspector.App.ViewModels;

/// <summary>
/// One row of the session popover (and of the app menu's Switch session ▸): a session the inspector endpoint lists,
/// with its words formatted once per refresh (a handful of rows, refreshed about once a second while shown).
/// </summary>
public sealed class SessionChoice
{
    internal SessionChoice(InspectorSessionInfo info, string detail, string uptime, bool isAttached, bool isRecording)
    {
        Info = info;
        Detail = detail;
        Uptime = uptime;
        IsAttached = isAttached;
        IsRecording = isRecording;
    }

    /// <summary>The session as the endpoint listed it.</summary>
    public InspectorSessionInfo Info { get; }

    /// <summary>"0x3A21".</summary>
    public string IdText => LiveDocumentViewModel.SessionHex(Info.Id);

    /// <summary>"192.168.1.40 · Tcp · chat": remote address, transport, endpoint name, as far as known.</summary>
    public string Detail { get; }

    /// <summary>How long it has been connected: "12:04", "1:02:03".</summary>
    public string Uptime { get; }

    /// <summary>True for the session the live document is attached to (the highlighted row).</summary>
    public bool IsAttached { get; }

    /// <summary>True while Record all writes it (the row's REC).</summary>
    public bool IsRecording { get; }

    /// <summary>True once the handshake completed (the green dot); false while handshaking.</summary>
    public bool IsConnected => Info.State == InspectorSessionState.Connected;

    /// <summary>The app menu's Switch session ▸ item: "0x3A21 · 192.168.1.40".</summary>
    public string MenuLabel => Detail.Length == 0 ? IdText : IdText + " · " + Detail;
}

/// <summary>
/// The title bar's session area (controls/window-title-bar items 6 and 7) and the session popover under it
/// (inspect-session-start): the live connection's status, endpoint and attached session, the record button (the REC
/// timer since attach, or "ALL · n" while every session is recorded), and the popover's list of the server's sessions
/// with attach / switch and "Record all sessions" (Ctrl+Shift+R). It follows the active document when that is live,
/// else the live document used last. Record all streams on a second connection to the same endpoint (one stream per
/// connection) into a <c>.nntl</c> file under <see cref="RecordingDirectory"/>; the attach keeps streaming beside it.
/// When the server disallows recording, the record controls stay visible but disabled with their tooltip (OQ4).
/// </summary>
public sealed partial class SessionAreaViewModel : ObservableObject, IAsyncDisposable
{
    /// <summary>The popover's refresh and the REC timer's tick.</summary>
    public static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);

    /// <summary>How long a stop waits for the endpoint to finish the recording (close blocks, footer).</summary>
    public static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(10);

    /// <summary>The record controls' tooltip while recording is allowed.</summary>
    public const string RecordAllTooltip = "Record all sessions (Ctrl+Shift+R)";

    private readonly DocumentsViewModel _documents;
    private readonly IUiDispatcher _ui;
    private readonly TimeProvider _time;
    private ITimer? _timer;
    private PendingChoice? _pending;
    private RecordAllRun? _recording;
    private bool _refreshing;

    /// <param name="documents">The open documents; the area follows their live ones.</param>
    public SessionAreaViewModel(DocumentsViewModel documents)
    {
        ArgumentNullException.ThrowIfNull(documents);
        _documents = documents;
        _ui = documents.Ui;
        _time = documents.Time;
        documents.PropertyChanged += OnDocumentsChanged;
        documents.Documents.CollectionChanged += OnDocumentListChanged;
        Track();
    }

    /// <summary>The live document shown: the active one when live, else the live one active last; null when none is open.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLive), nameof(HasConnection), nameof(RoleLabel), nameof(EndpointText), nameof(SessionText),
        nameof(SessionDetail), nameof(RecordingAllowed), nameof(RecordTooltip), nameof(PolicyText), nameof(IsOnline))]
    [NotifyCanExecuteChangedFor(nameof(RecordAllCommand))]
    public partial LiveDocumentViewModel? Live { get; private set; }

    /// <summary>True when a live document is shown: the record button and the time window's live state show.</summary>
    public bool HasLive => Live is not null;

    /// <summary>True when the connection group shows: a live document, or a Connect waiting for its session pick.</summary>
    public bool HasConnection => Live is not null || _pending is not null;

    /// <summary>"Server" or "Client": what the endpoint inspects.</summary>
    public string RoleLabel => (Live?.ServerInfo.Role ?? _pending?.Role) == NntlSessionRole.Client ? "Client" : "Server";

    /// <summary>The endpoint without its scheme: "10.0.4.12:7711".</summary>
    public string EndpointText => Live is { } live ? Display(live.Endpoint) : _pending?.Endpoint ?? "";

    /// <summary>The attached session's id ("0x3A21"); "—" before one is attached.</summary>
    public string SessionText => Live?.AttachedSession is { } s ? LiveDocumentViewModel.SessionHex(s.Id) : "—";

    /// <summary>What follows the id: the session's transport ("Tcp"); empty when unknown.</summary>
    public string SessionDetail => Live?.AttachedSession is { } s && s.TransportName.Length > 0 ? s.TransportName : "";

    /// <summary>True while the live document streams (the green status dot).</summary>
    public bool IsOnline => Live?.State is LiveState.Live or LiveState.Attaching;

    /// <summary>True when the inspected side allows recording (OQ4).</summary>
    public bool RecordingAllowed => Live?.RecordingAllowed ?? _pending?.RecordingAllowed ?? false;

    /// <summary>The record controls' tooltip: the shortcut, or why they are disabled.</summary>
    public string RecordTooltip => RecordingAllowed ? RecordAllTooltip : LiveDocumentViewModel.RecordingDisabledReason;

    /// <summary>The popover header's policy: "Recording enabled by server config" or disabled.</summary>
    public string PolicyText => RecordingAllowed ? "Recording enabled by server config" : LiveDocumentViewModel.RecordingDisabledReason;

    /// <summary>The server's sessions, as last listed.</summary>
    public ObservableCollection<SessionChoice> Sessions { get; } = [];

    /// <summary>"3 connected".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RecordAllDetail))]
    public partial string ConnectedText { get; private set; } = "";

    /// <summary>The session popover is open.</summary>
    [ObservableProperty]
    public partial bool IsPopoverOpen { get; set; }

    /// <summary>True while every session is being recorded.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RecordText), nameof(RecordAllLabel), nameof(RecordAllDetail))]
    public partial bool IsRecordingAll { get; private set; }

    /// <summary>Sessions the recording holds so far (the "3" of "ALL · 3").</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RecordText), nameof(RecordAllDetail))]
    public partial int RecordedCount { get; private set; }

    /// <summary>The REC timer: time since the attach ("00:42"), or how long Record all has run.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RecordText), nameof(RecordAllDetail))]
    public partial string ElapsedText { get; private set; } = "00:00";

    /// <summary>The record button: "ALL · 3" while every session is recorded, otherwise the REC timer.</summary>
    public string RecordText => IsRecordingAll ? "ALL · " + RecordedCount.ToString(CultureInfo.InvariantCulture) : ElapsedText;

    /// <summary>The popover's action: "Record all sessions", or "Stop recording" while it runs.</summary>
    public string RecordAllLabel => IsRecordingAll ? "Stop recording" : "Record all sessions";

    /// <summary>"3 now + any that connect", or while recording "3 sessions · 00:12".</summary>
    public string RecordAllDetail => IsRecordingAll
        ? RecordedCount.ToString(CultureInfo.InvariantCulture) + (RecordedCount == 1 ? " session · " : " sessions · ") + ElapsedText
        : Sessions.Count.ToString(CultureInfo.InvariantCulture) + " now + any that connect";

    /// <summary>The last recording Record all wrote; null before one.</summary>
    [ObservableProperty]
    public partial string? LastRecording { get; private set; }

    /// <summary>Why the last Record all or session switch failed; null when it did not.</summary>
    [ObservableProperty]
    public partial string? Error { get; private set; }

    /// <summary>Where Record all writes its files; the user's documents folder by default.</summary>
    public Func<string> RecordingDirectory { get; set; } = DefaultRecordingDirectory;

    /// <summary>NexNet Inspector/Recordings under the user's documents folder.</summary>
    public static string DefaultRecordingDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "NexNet Inspector", "Recordings");

    /// <summary>"10.0.4.12:7711": an endpoint as the title bar shows it, without its scheme.</summary>
    public static string Display(EndpointAddress address) => address.Scheme == EndpointScheme.Uds
        ? address.Host
        : (address.Host.Contains(':') ? "[" + address.Host + "]" : address.Host) + ":" + address.Port.ToString(CultureInfo.InvariantCulture);

    /// <summary>The REC timer: "00:42", "12:04", "1:02:03" (minutes always two digits, hours once there are any).</summary>
    public static string Clock(TimeSpan span)
    {
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        return span.TotalHours >= 1
            ? ((int)span.TotalHours).ToString(CultureInfo.InvariantCulture) + ":" + span.ToString(@"mm\:ss", CultureInfo.InvariantCulture)
            : ((int)span.TotalMinutes).ToString("00", CultureInfo.InvariantCulture) + ":" + span.Seconds.ToString("00", CultureInfo.InvariantCulture);
    }

    /// <summary>A session's uptime as the popover lists it: "0:42", "12:04", "1:02:03".</summary>
    public static string Uptime(TimeSpan span)
    {
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        return span.TotalHours >= 1
            ? ((int)span.TotalHours).ToString(CultureInfo.InvariantCulture) + ":" + span.ToString(@"mm\:ss", CultureInfo.InvariantCulture)
            : ((int)span.TotalMinutes).ToString(CultureInfo.InvariantCulture) + ":" + span.Seconds.ToString("00", CultureInfo.InvariantCulture);
    }

    /// <summary>The Session ▾ button: opens or closes the popover (and lists the sessions again).</summary>
    [RelayCommand]
    private void TogglePopover() => IsPopoverOpen = !IsPopoverOpen;

    /// <summary>
    /// The Connect dialog's session chooser (replacing the default that took the first session): shows the connection
    /// group for <paramref name="connect"/>'s endpoint and the popover over <paramref name="sessions"/>, and answers the
    /// session clicked, or null when the popover closes without one (the dialog stays).
    /// </summary>
    public Task<InspectorSessionInfo?> ChooseAsync(ConnectViewModel connect, IReadOnlyList<InspectorSessionInfo> sessions)
    {
        ArgumentNullException.ThrowIfNull(connect);
        ArgumentNullException.ThrowIfNull(sessions);
        _pending?.Answer.TrySetResult(null);
        var endpoint = EndpointAddress.TryParse(connect.Endpoint, connect.Transport, out var address, out _) ? Display(address) : connect.Endpoint.Trim();
        var pending = new PendingChoice(endpoint, connect.ServerInfo?.Role ?? NntlSessionRole.Server, connect.ServerInfo?.AllowRecording ?? false);
        _pending = pending;
        Fill(sessions, attached: null);
        RaiseConnection();
        IsPopoverOpen = true;
        return pending.Answer.Task;
    }

    /// <summary>A row was clicked: answers a waiting Connect, or switches the live document to that session.</summary>
    [RelayCommand]
    private async Task Choose(SessionChoice choice)
    {
        ArgumentNullException.ThrowIfNull(choice);
        if (_pending is { } pending)
        {
            _pending = null;
            IsPopoverOpen = false;
            pending.Answer.TrySetResult(choice.Info);
            RaiseConnection();
            return;
        }

        IsPopoverOpen = false;
        if (Live is not { } live || (live.AttachedSession?.Id == choice.Info.Id && live.IsStreaming))
            return;

        // Attaching elsewhere drops what the document holds: unsaved data asks first, as closing it does (OQ5).
        if (!await _documents.ConfirmDiscardAsync(live).ConfigureAwait(true))
            return;

        // Detach first: the endpoint streams one attach per connection and only frees it once the old stream finished.
        var request = live.Request ?? new InspectorAttachRequest(InspectorTargetKind.Session, 0, _documents.ConnectDefaults.Overflow,
            _documents.ConnectDefaults.Snapshot, Record: false);
        Error = null;
        try
        {
            await live.DetachAsync().ConfigureAwait(true);
            await live.AttachAsync(request with { Target = InspectorTargetKind.Session, SessionId = choice.Info.Id }, choice.Info)
                .ConfigureAwait(true);
        }
        catch (Exception e) when (e is InspectorRequestException or InspectorConnectionException or IOException or OperationCanceledException)
        {
            Error = e.Message;
        }

        RaiseConnection();
        await RefreshSessionsAsync().ConfigureAwait(true);
    }

    /// <summary>Ctrl+Shift+R, the record button and the popover's action: starts recording every session, or stops it.</summary>
    [RelayCommand(CanExecute = nameof(CanRecordAll))]
    private async Task RecordAll()
    {
        if (_recording is { } run)
            await StopRecordingAsync(run).ConfigureAwait(true);
        else
            await StartRecordingAsync().ConfigureAwait(true);
    }

    private bool CanRecordAll() => Live is { RecordingAllowed: true } || _recording is not null;

    /// <summary>Lists the server's sessions again (the popover's rows, the Switch session ▸ items).</summary>
    public async Task RefreshSessionsAsync()
    {
        if (_pending is not null || Live is not { } live || _refreshing)
            return;

        _refreshing = true;
        try
        {
            var sessions = await live.Connection.ListSessionsAsync().WaitAsync(DrainTimeout).ConfigureAwait(true);
            if (!ReferenceEquals(Live, live))
                return;
            if (_recording is { } run)
            {
                foreach (var session in sessions)
                    run.Sessions.Add(session.Id);
                RecordedCount = run.Sessions.Count;
            }

            Fill(sessions, live.AttachedSession?.Id);
        }
        catch (Exception e) when (e is InspectorConnectionException or IOException or TimeoutException or InvalidOperationException
                                      or ObjectDisposedException or TaskCanceledException)
        {
            // The endpoint went away: the session-ended banner speaks for it; the rows keep their last state.
        }
        finally
        {
            _refreshing = false;
        }
    }

    /// <summary>
    /// Finishes a recording in progress (application exit) so the file gets its footer and goes into the recents. It does
    /// not resume on the UI thread, so the exit handler can wait for it there.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        _documents.PropertyChanged -= OnDocumentsChanged;
        _documents.Documents.CollectionChanged -= OnDocumentListChanged;
        StopTimer();
        _pending?.Answer.TrySetResult(null);
        if (_recording is not { } run)
            return;
        _recording = null;
        await FinishAsync(run, _time).ConfigureAwait(false);
        _documents.AddRecentRecording(run.Path, run.Sessions.Count);
    }

    partial void OnIsPopoverOpenChanged(bool value)
    {
        if (value)
        {
            _ = RefreshSessionsAsync();
            return;
        }

        // Closed without a pick: the Connect dialog stays open for another try.
        if (_pending is { } pending)
        {
            _pending = null;
            pending.Answer.TrySetResult(null);
            RaiseConnection();
        }
    }

    partial void OnLiveChanged(LiveDocumentViewModel? oldValue, LiveDocumentViewModel? newValue)
    {
        if (oldValue is not null) oldValue.PropertyChanged -= OnLivePropertyChanged;
        if (newValue is not null) newValue.PropertyChanged += OnLivePropertyChanged;
        Sessions.Clear();
        ConnectedText = "";
        Tick();
        if (newValue is not null || _recording is not null) StartTimer();
        else StopTimer();
    }

    private void OnLivePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(LiveDocumentViewModel.AttachedSession) or nameof(LiveDocumentViewModel.State):
                OnPropertyChanged(nameof(SessionText));
                OnPropertyChanged(nameof(SessionDetail));
                OnPropertyChanged(nameof(IsOnline));
                break;
            case nameof(LiveDocumentViewModel.AttachedAt):
                Tick();
                break;
        }
    }

    private void OnDocumentsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DocumentsViewModel.Active)) Track();
    }

    private void OnDocumentListChanged(object? sender, NotifyCollectionChangedEventArgs e) => Track();

    // The active live document, else the live one active most recently (RecentOrder), else none.
    private void Track()
    {
        LiveDocumentViewModel? live = _documents.Active as LiveDocumentViewModel;
        if (live is null)
        {
            foreach (var document in _documents.RecentOrder)
            {
                if (document is LiveDocumentViewModel recent)
                {
                    live = recent;
                    break;
                }
            }
        }

        Live = live;
    }

    private void Fill(IReadOnlyList<InspectorSessionInfo> sessions, long? attached)
    {
        var now = _time.GetUtcNow().UtcTicks;
        Sessions.Clear();
        var connected = 0;
        foreach (var session in sessions)
        {
            if (session.State == InspectorSessionState.Connected) connected++;
            var uptime = session.StartedUtc > 0 ? Uptime(TimeSpan.FromTicks(now - session.StartedUtc)) : "";
            var recording = _recording is { } run && run.Sessions.Contains(session.Id);
            Sessions.Add(new SessionChoice(session, Describe(session), uptime, session.Id == attached, recording));
        }

        ConnectedText = connected.ToString(CultureInfo.InvariantCulture) + " connected";
        OnPropertyChanged(nameof(RecordAllDetail));
    }

    private static string Describe(in InspectorSessionInfo session)
    {
        var text = session.RemoteEndpoint ?? "";
        if (session.TransportName.Length > 0) text = text.Length == 0 ? session.TransportName : text + " · " + session.TransportName;
        // A server endpoint is often named after its transport ("tcp"); the name adds nothing then.
        if (session.Endpoint is { Length: > 0 } endpoint && !endpoint.Equals(session.TransportName, StringComparison.OrdinalIgnoreCase))
            text = text.Length == 0 ? endpoint : text + " · " + endpoint;
        return text;
    }

    private async Task StartRecordingAsync()
    {
        if (Live is not { RecordingAllowed: true } live)
            return;

        Error = null;
        InspectorConnection? connection = null;
        InspectorStream? stream = null;
        try
        {
            connection = await live.Connection.ConnectAgainAsync().ConfigureAwait(true);
            var defaults = _documents.ConnectDefaults;
            stream = await connection.Client.RecordAllAsync(new InspectorRecordRequest(live.Request?.Overflow ?? defaults.Overflow, Snapshot: true))
                .ConfigureAwait(true);
            var directory = RecordingDirectory();
            Directory.CreateDirectory(directory);
            var path = UniquePath(directory, _time.GetLocalNow());
            var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 64 * 1024, FileOptions.Asynchronous);
            var run = new RecordAllRun(connection, stream, file, path, _time.GetUtcNow());
            foreach (var session in Sessions)
                run.Sessions.Add(session.Info.Id);
            _recording = run;

            // Not cancelable: a stop asks the endpoint to end the stream, which then sends the close blocks and the footer
            // the copy must still write.
            run.Copy = stream.CopyToAsync(file, CancellationToken.None);
            _ = WatchAsync(run);
            IsRecordingAll = true;
            RecordedCount = run.Sessions.Count;
            StartTimer();
            Tick();
            RecordAllCommand.NotifyCanExecuteChanged();
            await RefreshSessionsAsync().ConfigureAwait(true);
        }
        catch (Exception e) when (e is InspectorRequestException or InspectorConnectionException or IOException or UnauthorizedAccessException
                                      or System.Net.Sockets.SocketException or TimeoutException or OperationCanceledException)
        {
            Error = e.Message;
            if (stream is not null) await stream.DisposeAsync().ConfigureAwait(true);
            if (connection is not null) await connection.DisposeAsync().ConfigureAwait(true);
        }
    }

    // A stream the endpoint ended by itself (it shut down) finishes the recording the same way as a stop.
    private async Task WatchAsync(RecordAllRun run)
    {
        try
        {
            await run.Copy!.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The pipe failed or was released; StopRecordingAsync keeps whatever arrived.
        }

        _ui.Post(() =>
        {
            if (ReferenceEquals(_recording, run)) _ = StopRecordingAsync(run);
        });
    }

    private async Task StopRecordingAsync(RecordAllRun run)
    {
        if (!ReferenceEquals(_recording, run))
            return;
        _recording = null;
        await FinishAsync(run, _time).ConfigureAwait(true);
        _documents.AddRecentRecording(run.Path, run.Sessions.Count);
        LastRecording = run.Path;
        IsRecordingAll = false;
        RecordAllCommand.NotifyCanExecuteChanged();
        if (Live is null) StopTimer();
        Tick();
        Fill([.. Sessions.Select(static s => s.Info)], Live?.AttachedSession?.Id);
    }

    // Asks the endpoint to end the stream, lets the copy write the close blocks and footer it then sends, and releases
    // everything. Never resumes on the UI thread: application exit waits for it there.
    private static async Task FinishAsync(RecordAllRun run, TimeProvider time)
    {
        try
        {
            if (!run.Copy!.IsCompleted)
            {
                try
                {
                    await run.Connection.Client.DetachAsync().WaitAsync(DrainTimeout).ConfigureAwait(false);
                }
                catch (Exception e) when (e is TimeoutException or IOException or InvalidOperationException or InspectorConnectionException)
                {
                    // The endpoint is gone or slow; the copy keeps what arrived.
                }

                if (await Task.WhenAny(run.Copy, Task.Delay(DrainTimeout, time)).ConfigureAwait(false) != run.Copy)
                    await run.Stream.DisposeAsync().ConfigureAwait(false);
            }

            try
            {
                await run.Copy.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Released under the copy, or the pipe broke: the file holds what arrived, which a reader can still open.
            }
        }
        finally
        {
            await run.File.DisposeAsync().ConfigureAwait(false);
            await run.Stream.DisposeAsync().ConfigureAwait(false);
            await run.Connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static string UniquePath(string directory, DateTimeOffset now)
    {
        var stem = "server-all-" + now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var path = Path.Combine(directory, stem + ".nntl");
        for (var i = 2; File.Exists(path); i++)
            path = Path.Combine(directory, stem + "-" + i.ToString(CultureInfo.InvariantCulture) + ".nntl");
        return path;
    }

    private void StartTimer() => _timer ??= _time.CreateTimer(static state => ((SessionAreaViewModel)state!).OnTimer(), this, TickInterval, TickInterval);

    private void StopTimer()
    {
        _timer?.Dispose();
        _timer = null;
    }

    // Timer thread: the tick runs on the UI thread.
    private void OnTimer() => _ui.Post(() =>
    {
        Tick();
        if (IsPopoverOpen || _recording is not null) _ = RefreshSessionsAsync();
    });

    private void Tick()
    {
        var now = _time.GetUtcNow();
        if (_recording is { } run) ElapsedText = Clock(now - run.Started);
        else ElapsedText = Live?.AttachedAt is { } at ? Clock(now - at) : "00:00";
    }

    private void RaiseConnection()
    {
        OnPropertyChanged(nameof(HasConnection));
        OnPropertyChanged(nameof(RoleLabel));
        OnPropertyChanged(nameof(EndpointText));
        OnPropertyChanged(nameof(RecordingAllowed));
        OnPropertyChanged(nameof(RecordTooltip));
        OnPropertyChanged(nameof(PolicyText));
        OnPropertyChanged(nameof(SessionText));
    }

    private sealed class PendingChoice(string endpoint, NntlSessionRole role, bool recordingAllowed)
    {
        public readonly TaskCompletionSource<InspectorSessionInfo?> Answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly string Endpoint = endpoint;
        public readonly NntlSessionRole Role = role;
        public readonly bool RecordingAllowed = recordingAllowed;
    }

    private sealed class RecordAllRun(InspectorConnection connection, InspectorStream stream, FileStream file, string path, DateTimeOffset started)
    {
        public readonly InspectorConnection Connection = connection;
        public readonly InspectorStream Stream = stream;
        public readonly FileStream File = file;
        public readonly string Path = path;
        public readonly DateTimeOffset Started = started;
        public readonly HashSet<long> Sessions = [];
        public Task? Copy;
    }
}
