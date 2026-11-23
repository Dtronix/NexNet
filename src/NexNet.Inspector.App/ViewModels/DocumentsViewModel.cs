using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NexNet.Inspector.App.Model;
using NexNet.Inspector.App.Services;
using NexNet.Inspector.App.Settings;
using NexNet.Inspector.Core.Format;
using NexNet.Inspector.Core.Model;
using NexNet.Inspector.Core.Schema;
using NexNet.Inspector.Core.Session;
using NexNet.Inspector.Tap;

namespace NexNet.Inspector.App.ViewModels;

/// <summary>The session and schema to open from a recording (the open-recording dialog's answer, I24).</summary>
/// <param name="SessionId">The session.</param>
/// <param name="Schema">Embedded, workspace or raw.</param>
public readonly record struct RecordingOpenChoice(long SessionId, SchemaChoice Schema);

/// <summary>
/// The open documents (the tab strip's model, I16): open, activate, close, and Ctrl+Tab through them in most recently
/// used order. Owns the <see cref="SyncBus"/> all views follow; switching tabs keeps the window's zoom and position
/// relative to each capture's start, and each document keeps its own selection.
/// </summary>
public sealed partial class DocumentsViewModel : ObservableObject, IAsyncDisposable
{
    /// <summary>The window a new live document shows: the last 50 ms, following the live edge.</summary>
    public static readonly TimeSpan DefaultLiveWindow = TimeSpan.FromMilliseconds(50);

    private readonly IUiDispatcher _ui;
    private readonly SettingsStore? _store;
    private readonly TimeProvider _time;
    private readonly List<DocumentViewModel> _recent = [];
    private ConnectDefaults? _connectDefaults;

    /// <param name="ui">Where document refreshes and sync notifications run.</param>
    /// <param name="store">Where recents and connect defaults are saved; null keeps them for this run only.</param>
    /// <param name="time">Clock of the recents and the probe debounce; the system clock by default.</param>
    public DocumentsViewModel(IUiDispatcher ui, SettingsStore? store, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(ui);
        _ui = ui;
        _store = store;
        _time = time ?? TimeProvider.System;
        Sync = new SyncBus(ui) { TraceResolver = ResolveTrace };
    }

    /// <summary>The clock of recents, the probe debounce and the live REC timer.</summary>
    internal TimeProvider Time => _time;

    /// <summary>Where posted refreshes run.</summary>
    internal IUiDispatcher Ui => _ui;

    /// <summary>The time window, playhead and selection every view follows.</summary>
    public SyncBus Sync { get; }

    /// <summary>The documents, in tab order.</summary>
    public ObservableCollection<DocumentViewModel> Documents { get; } = [];

    /// <summary>Documents from most to least recently active (Ctrl+Tab order).</summary>
    public IReadOnlyList<DocumentViewModel> RecentOrder => _recent;

    /// <summary>The active document.</summary>
    [ObservableProperty]
    public partial DocumentViewModel? Active { get; private set; }

    /// <summary>True when at least one document is open (otherwise the window shows its empty state).</summary>
    public bool HasDocuments => Documents.Count > 0;

    /// <summary>The workspace schema the user set; new documents compare their embedded schema with it.</summary>
    public NexusSchema? WorkspaceSchema { get; set; }

    /// <summary>Where the workspace schema came from (its path), as the open-recording dialog names it.</summary>
    public string? WorkspaceSchemaName { get; set; }

    /// <summary>
    /// Asked before closing a document with unsaved data (OQ5); I29 shows Save .nntl… / Discard / Cancel. Without it a
    /// dirty document closes as if discarded.
    /// </summary>
    public Func<DocumentViewModel, Task<CloseDecision>>? ClosePrompt { get; set; }

    /// <summary>
    /// Saves a document (Ctrl+S, the close prompt's Save, the session-ended banner); true when it was saved. The window
    /// binds it to <see cref="SaveAsync(DocumentViewModel, CancellationToken)"/>.
    /// </summary>
    public Func<DocumentViewModel, Task<bool>>? SaveHandler { get; set; }

    /// <summary>
    /// Asks where to save (the window's save picker), given a suggested file name; null answers cancel. Without it
    /// nothing can be saved.
    /// </summary>
    public Func<string, Task<string?>>? SavePicker { get; set; }

    /// <summary>Why the last save failed; null when it did not (or none was tried).</summary>
    [ObservableProperty]
    public partial string? SaveError { get; private set; }

    /// <summary>
    /// Picks the session (and schema) of a recording that needs asking (<see cref="NeedsPicker"/>); the open-recording
    /// dialog (I24). Null answers cancel. Without it the first session opens with its embedded schema.
    /// </summary>
    public Func<RecordingFile, Task<RecordingOpenChoice?>>? SessionPicker { get; set; }

    /// <summary>
    /// The open-recording dialog's "Compare two sessions…" (P4 binds it): true when Compare took over. The recording is
    /// disposed once the dialog closes, so Compare opens the sessions it compares from the file's path.
    /// </summary>
    public Func<RecordingFile, Task<bool>>? SessionComparer { get; set; }

    /// <summary>
    /// True when opening <paramref name="recording"/> asks <see cref="SessionPicker"/> (flow 4, Open a recording): it
    /// holds several sessions, it is damaged or was never closed (OQ3), or its first session's schema does not simply
    /// decode it (differs from the workspace schema, is unreadable, or is missing).
    /// </summary>
    public bool NeedsPicker(RecordingFile recording)
    {
        ArgumentNullException.ThrowIfNull(recording);
        return recording.Sessions.Count > 1
               || recording.Warnings != RecordingWarnings.None
               || (recording.Sessions.Count == 1
                   && recording.ResolveSchema(recording.Sessions[0], WorkspaceSchema, SchemaChoice.Embedded).Status != SchemaStatus.Embedded);
    }

    /// <summary>The Connect dialog's model, starting from the saved defaults and recent endpoints.</summary>
    public ConnectViewModel CreateConnect()
    {
        var settings = _store?.Current ?? new AppSettings();
        return new ConnectViewModel(this, ConnectDefaults, settings.RecentEndpoints, _time);
    }

    /// <summary>
    /// Opens one session of a recording as a new document, activates it and indexes it in the background. The file is
    /// read (header, index) on the thread pool.
    /// </summary>
    /// <param name="path">The recording.</param>
    /// <param name="choice">The session and schema; null asks <see cref="SessionPicker"/> when <see cref="NeedsPicker"/>.</param>
    /// <param name="cancellationToken">Cancels opening.</param>
    /// <returns>The document; null when the picker was canceled.</returns>
    /// <exception cref="NntlFormatException">Not a recording.</exception>
    /// <exception cref="IOException">The file cannot be read.</exception>
    public async Task<FileDocumentViewModel?> OpenRecordingAsync(string path, RecordingOpenChoice? choice = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var recording = await Task.Run(() => RecordingFile.Open(path), cancellationToken).ConfigureAwait(true);
        try
        {
            if (recording.Sessions.Count == 0)
                throw new NntlFormatException("The recording holds no session.");

            if (choice is null && SessionPicker is { } picker && NeedsPicker(recording))
                choice = await picker(recording).ConfigureAwait(true);
            else
                choice ??= new RecordingOpenChoice(recording.Sessions[0].SessionId, SchemaChoice.Embedded);

            if (choice is not { } picked)
            {
                recording.Dispose();
                return null;
            }

            var session = recording.CreateSession(picked.SessionId, WorkspaceSchema, picked.Schema);
            var document = new FileDocumentViewModel(recording, session, _ui);
            Add(document);
            document.StartIndexing();
            _store?.Update(s => s.WithRecentFile(recording.Path ?? path, recording.Sessions.Count, _time.GetUtcNow()));
            return document;
        }
        catch
        {
            recording.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Attaches to a session through a connection and opens it as a new live document, which owns the connection from
    /// now on. On success the endpoint goes first in the recents and the Connect choices become the defaults.
    /// </summary>
    /// <param name="connection">The endpoint connection.</param>
    /// <param name="session">The session to attach to; null for a client's single connection.</param>
    /// <param name="overflow">Drop or Block.</param>
    /// <param name="snapshot">Request a state snapshot on attach.</param>
    /// <param name="cancellationToken">Cancels the attach.</param>
    /// <exception cref="InspectorRequestException">The endpoint refused.</exception>
    public async Task<LiveDocumentViewModel> AttachAsync(InspectorConnection connection, InspectorSessionInfo? session,
        InspectorOverflow overflow, bool snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var target = session is null ? InspectorTargetKind.Client : InspectorTargetKind.Session;
        var request = new InspectorAttachRequest(target, session?.Id ?? 0, overflow, snapshot, Record: false);
        var document = new LiveDocumentViewModel(connection, _ui, WorkspaceSchema, _time);
        try
        {
            await document.AttachAsync(request, session, cancellationToken).ConfigureAwait(true);
        }
        catch
        {
            await document.DisposeAsync().ConfigureAwait(true);
            throw;
        }

        Add(document);
        var endpoint = connection.Address.ToString();
        _store?.Update(s => s.WithRecentEndpoint(endpoint, target, _time.GetUtcNow()) with
        {
            Connect = new ConnectDefaults { Endpoint = endpoint, Target = target, Overflow = overflow, Snapshot = snapshot },
        });
        return document;
    }

    /// <summary>Puts a recording written outside a document (Record all) first in the recent files.</summary>
    /// <param name="path">The recording.</param>
    /// <param name="sessions">Sessions it holds.</param>
    public void AddRecentRecording(string path, int sessions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _store?.Update(s => s.WithRecentFile(path, sessions, _time.GetUtcNow()));
    }

    /// <summary>The connect defaults saved by the last attach (or the app menu's overflow mode).</summary>
    public ConnectDefaults ConnectDefaults => _store?.Current.Connect ?? _connectDefaults ?? ConnectDefaults.Default;

    /// <summary>Sets the overflow mode later attaches use (the app menu's Overflow mode ▸).</summary>
    public void SetOverflow(InspectorOverflow overflow)
    {
        var defaults = ConnectDefaults with { Overflow = overflow };
        _connectDefaults = defaults;
        _store?.Update(s => s with { Connect = defaults });
        OnPropertyChanged(nameof(ConnectDefaults));
    }

    /// <summary>Makes a document the active one: the sync bus takes its window, playhead and selection.</summary>
    [RelayCommand]
    public void Activate(DocumentViewModel document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!Documents.Contains(document))
            throw new ArgumentException("The document is not open.", nameof(document));
        if (Active == document)
            return;

        var previous = Active;
        if (previous is not null)
            SaveViewState(previous);

        _recent.Remove(document);
        _recent.Insert(0, document);
        Active = document;
        RestoreViewState(document, previous);
    }

    /// <summary>Ctrl+Tab (forward) and Ctrl+Shift+Tab: the next document in most recently used order.</summary>
    /// <param name="backwards">True for Ctrl+Shift+Tab.</param>
    [RelayCommand]
    public void Cycle(bool backwards)
    {
        if (_recent.Count < 2)
            return;
        // Activation reorders the list, so the target is picked first.
        var target = backwards ? _recent[^1] : _recent[1];
        Activate(target);
    }

    /// <summary>
    /// Closes a document. One with unsaved data asks <see cref="ClosePrompt"/> first (OQ5), unless
    /// <paramref name="ask"/> is false (the session-ended banner's Discard, which is the answer already).
    /// </summary>
    /// <returns>False when the user canceled.</returns>
    public async Task<bool> CloseAsync(DocumentViewModel document, bool ask = true)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!Documents.Contains(document))
            return true;

        if (ask && !await ConfirmDiscardAsync(document).ConfigureAwait(true))
            return false;

        Remove(document);
        await document.DisposeAsync().ConfigureAwait(true);
        return true;
    }

    /// <summary>
    /// Before dropping a document's unsaved data (closing it, attaching it to another session): asks
    /// <see cref="ClosePrompt"/> Save .nntl… / Discard / Cancel, and saves on Save. A clean document needs no answer.
    /// </summary>
    /// <returns>True when the data may go: it was saved, discarded, or there was nothing to lose.</returns>
    public async Task<bool> ConfirmDiscardAsync(DocumentViewModel document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!document.IsDirty || ClosePrompt is not { } prompt)
            return true;

        switch (await prompt(document).ConfigureAwait(true))
        {
            case CloseDecision.Cancel:
                return false;
            case CloseDecision.Save when SaveHandler is { } save:
                return await save(document).ConfigureAwait(true);
            default:
                return true;
        }
    }

    /// <summary>
    /// Save .nntl…: asks <see cref="SavePicker"/> where, then writes the document's session there as a one-session
    /// recording (<see cref="SaveToAsync"/>). A failure is reported in <see cref="SaveError"/>.
    /// </summary>
    /// <returns>The path saved to; null when canceled, failed, or there is nothing to save.</returns>
    public async Task<string?> SaveAsync(DocumentViewModel document, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        SaveError = null;
        if (document.Session is not { } session || SavePicker is not { } pick)
            return null;
        if (await pick(SuggestedFileName(document, session, _time.GetLocalNow())).ConfigureAwait(true) is not { Length: > 0 } path)
            return null;

        try
        {
            await SaveToAsync(document, path, cancellationToken).ConfigureAwait(true);
            return path;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NntlFormatException or ArgumentException)
        {
            SaveError = "Could not save " + Path.GetFileName(path) + ": " + e.Message;
            return null;
        }
    }

    /// <summary>
    /// Writes a document's session to <paramref name="path"/> as a one-session <c>.nntl</c> recording, on the thread
    /// pool (a file still indexing is saved once its index completes). The file appears whole or not at all: it is
    /// written beside the target and moved over it. A live document is marked saved and the file goes first in the
    /// recents.
    /// </summary>
    /// <exception cref="IOException">The file could not be written.</exception>
    public async Task SaveToAsync(DocumentViewModel document, string path, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (document.Session is not { } session)
            throw new InvalidOperationException("The document holds no session.");
        if (document is FileDocumentViewModel file)
            await file.Indexing.ConfigureAwait(true);

        var full = Path.GetFullPath(path);
        var version = session.Version;
        await Task.Run(() =>
        {
            var partial = full + ".partial";
            try
            {
                using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024))
                    CaptureExport.Write(session, output, cancellationToken);
                File.Move(partial, full, overwrite: true);
            }
            catch
            {
                File.Delete(partial);
                throw;
            }
        }, cancellationToken).ConfigureAwait(true);

        if (document is LiveDocumentViewModel live)
            live.MarkSaved(full, version);
        _store?.Update(s => s.WithRecentFile(full, 1, _time.GetUtcNow()));
    }

    /// <summary>"server-0x3A21-20261123-120400.nntl": the side, the session and when it was saved.</summary>
    public static string SuggestedFileName(DocumentViewModel document, CaptureSession session, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(session);
        if (document is FileDocumentViewModel { Path: { } source })
            return Path.GetFileNameWithoutExtension(source) + "-" + LiveDocumentViewModel.SessionHex(session.SessionId) + ".nntl";
        var side = session.Role == NntlSessionRole.Client ? "client" : "server";
        return side + "-" + LiveDocumentViewModel.SessionHex(session.SessionId) + "-"
               + now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture) + ".nntl";
    }

    /// <summary>
    /// "File saved, tab becomes file" (session-ended flow): opens the recording a live document was saved to in its tab,
    /// with its selection and playhead, and closes the live document (its connection too). The view keeps its window:
    /// the file starts on the same clock.
    /// </summary>
    /// <returns>The file document.</returns>
    /// <exception cref="NntlFormatException">The file is not a recording.</exception>
    /// <exception cref="IOException">The file cannot be read.</exception>
    public async Task<FileDocumentViewModel> ReplaceWithRecordingAsync(LiveDocumentViewModel live, string path)
    {
        ArgumentNullException.ThrowIfNull(live);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var index = Documents.IndexOf(live);
        if (index < 0)
            throw new ArgumentException("The document is not open.", nameof(live));

        var recording = await Task.Run(() => RecordingFile.Open(path)).ConfigureAwait(true);
        FileDocumentViewModel document;
        try
        {
            if (recording.Sessions.Count == 0)
                throw new NntlFormatException("The recording holds no session.");
            var choice = live.Session?.Schema.Source switch
            {
                SchemaSource.Workspace => SchemaChoice.Workspace,
                SchemaSource.None when live.Session?.Schema.Embedded is not null => SchemaChoice.Raw,
                _ => SchemaChoice.Embedded,
            };
            document = new FileDocumentViewModel(recording, recording.CreateSession(recording.Sessions[0].SessionId, WorkspaceSchema, choice), _ui);
        }
        catch
        {
            recording.Dispose();
            throw;
        }

        var wasActive = Active == live;
        if (wasActive)
            SaveViewState(live);
        document.SavedSelection = live.SavedSelection;
        document.SavedPlayhead = live.SavedPlayhead;

        document.DataChanged += OnDocumentChanged;
        Documents.Insert(index, document);
        OnDocumentAdded(document);
        OnPropertyChanged(nameof(HasDocuments));
        if (wasActive)
            Activate(document);
        else
            _recent.Insert(Math.Max(0, _recent.IndexOf(live)), document);

        Remove(live);
        document.StartIndexing();
        await live.DisposeAsync().ConfigureAwait(true);
        return document;
    }

    // Takes a document out of the tabs; the next most recent becomes active when it was.
    private void Remove(DocumentViewModel document)
    {
        Documents.Remove(document);
        _recent.Remove(document);
        document.DataChanged -= OnDocumentChanged;
        OnDocumentRemoved(document);
        OnPropertyChanged(nameof(HasDocuments));
        if (Active == document)
        {
            Active = null;
            if (_recent.Count > 0)
                Activate(_recent[0]);
            else
                Sync.Restore(default, default, 0, Selection.None, false);
        }
    }

    /// <summary>The tab close button and Ctrl+W.</summary>
    [RelayCommand]
    private Task CloseDocument(DocumentViewModel document) => CloseAsync(document);

    /// <summary>Closes every document without asking (application exit after its own prompt).</summary>
    public async ValueTask DisposeAsync()
    {
        foreach (var document in Documents.ToArray())
        {
            document.DataChanged -= OnDocumentChanged;
            await document.DisposeAsync().ConfigureAwait(false);
        }

        Documents.Clear();
        _recent.Clear();
    }

    /// <summary>Adds an opened document as the last tab and activates it.</summary>
    internal void Add(DocumentViewModel document)
    {
        document.DataChanged += OnDocumentChanged;
        Documents.Add(document);
        OnDocumentAdded(document);
        OnPropertyChanged(nameof(HasDocuments));
        Activate(document);
    }

    // What a selection traces in the active document's session.
    private MessageTrace ResolveTrace(Selection selection)
    {
        if (Active?.Session is not { } session)
            return MessageTrace.None;
        lock (session.Gate)
            return MessageTrace.Resolve(session, selection);
    }

    private void OnDocumentChanged(DocumentViewModel document)
    {
        if (document != Active)
            return;

        if (document.Kind == DocumentKind.Live && document.Session is null)
        {
            // A detach dropped everything: no window, playhead or selection carries into the next attach, which starts
            // on its own live edge.
            if (!Sync.Window.IsEmpty || !Sync.Selection.IsNone || Sync.Playhead != 0)
                Sync.Restore(default, default, 0, Selection.None, followLive: true);
            return;
        }

        // A traced message still arriving (chunks, its result) grows with the data.
        Sync.RefreshTrace();

        var range = document.TimeRange;
        Sync.SetBounds(document.Kind == DocumentKind.Live ? default : range);
        if (document.Kind == DocumentKind.Live)
        {
            if (Sync.Window.IsEmpty && document.Session is { } session)
            {
                Sync.SetWindow(TimeWindow.FromDuration(session.StartTicks,
                    TimeWindow.TicksFromMilliseconds(DefaultLiveWindow.TotalMilliseconds, document.Frequency)), byUser: false);
                Sync.SetFollowLive(true);
            }

            if (!range.IsEmpty)
                Sync.OnLiveData(range.End);
        }
        else if (!range.IsEmpty && (Sync.Window.IsEmpty || Sync.Window == document.FittedWindow))
        {
            // A file being indexed grows; the view keeps fitting it until the user zooms or pans.
            document.FittedWindow = range;
            Sync.SetWindow(range, byUser: false);
        }
    }

    private void SaveViewState(DocumentViewModel document)
    {
        document.SavedPlayhead = Sync.Playhead;
        document.SavedSelection = Sync.Selection;
        document.SavedFollowLive = Sync.FollowLive;
    }

    private void RestoreViewState(DocumentViewModel document, DocumentViewModel? previous)
    {
        // All documents share one window: the same zoom and position relative to each capture's start. The first
        // document has nothing to carry over and starts fitted (a file) or on the live edge.
        var window = default(TimeWindow);
        if (previous is not null && !Sync.Window.IsEmpty && document.Session is { } to && previous.Session is { } from)
            window = Sync.Window.Rebase(from.StartTicks, previous.Frequency, to.StartTicks, document.Frequency);

        var bounds = document.Kind == DocumentKind.Live ? default : document.TimeRange;
        if (!window.IsEmpty && !bounds.IsEmpty)
            window = window.Clamp(bounds.Start, Math.Max(bounds.End, bounds.Start + window.Duration));

        var follow = document.Kind == DocumentKind.Live && document.SavedFollowLive;
        Sync.Restore(window, bounds, document.SavedPlayhead, document.SavedSelection, follow);
        if (window.IsEmpty)
            OnDocumentChanged(document);
    }
}
