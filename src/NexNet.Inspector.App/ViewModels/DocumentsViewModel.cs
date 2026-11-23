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

    /// <param name="ui">Where document refreshes and sync notifications run.</param>
    /// <param name="store">Where recents and connect defaults are saved; null keeps them for this run only.</param>
    /// <param name="time">Clock of the recents and the probe debounce; the system clock by default.</param>
    public DocumentsViewModel(IUiDispatcher ui, SettingsStore? store, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(ui);
        _ui = ui;
        _store = store;
        _time = time ?? TimeProvider.System;
        Sync = new SyncBus(ui);
    }

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

    /// <summary>
    /// Asked before closing a document with unsaved data (OQ5); I29 shows Save .nntl… / Discard / Cancel. Without it a
    /// dirty document closes as if discarded.
    /// </summary>
    public Func<DocumentViewModel, Task<CloseDecision>>? ClosePrompt { get; set; }

    /// <summary>Saves a live document before it closes (I29); called when the prompt answers Save.</summary>
    public Func<DocumentViewModel, Task<bool>>? SaveHandler { get; set; }

    /// <summary>
    /// Picks the session (and schema) of a multi-session recording; I24's open-recording dialog. Null answers cancel.
    /// Without it the first session opens with its embedded schema.
    /// </summary>
    public Func<RecordingFile, Task<RecordingOpenChoice?>>? SessionPicker { get; set; }

    /// <summary>The Connect dialog's model, starting from the saved defaults and recent endpoints.</summary>
    public ConnectViewModel CreateConnect()
    {
        var settings = _store?.Current ?? new AppSettings();
        return new ConnectViewModel(this, settings.Connect ?? ConnectDefaults.Default, settings.RecentEndpoints, _time);
    }

    /// <summary>
    /// Opens one session of a recording as a new document, activates it and indexes it in the background. The file is
    /// read (header, index) on the thread pool.
    /// </summary>
    /// <param name="path">The recording.</param>
    /// <param name="choice">The session and schema; null asks <see cref="SessionPicker"/> when the file has several sessions.</param>
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

            if (choice is null && recording.Sessions.Count > 1 && SessionPicker is { } picker)
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
        var document = new LiveDocumentViewModel(connection, _ui, WorkspaceSchema);
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
    /// Closes a document. One with unsaved data asks <see cref="ClosePrompt"/> first (OQ5).
    /// </summary>
    /// <returns>False when the user canceled.</returns>
    public async Task<bool> CloseAsync(DocumentViewModel document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!Documents.Contains(document))
            return true;

        if (document.IsDirty && ClosePrompt is { } prompt)
        {
            switch (await prompt(document).ConfigureAwait(true))
            {
                case CloseDecision.Cancel:
                    return false;
                case CloseDecision.Save when SaveHandler is { } save:
                    if (!await save(document).ConfigureAwait(true))
                        return false;
                    break;
            }
        }

        Documents.Remove(document);
        _recent.Remove(document);
        document.DataChanged -= OnDocumentChanged;
        OnPropertyChanged(nameof(HasDocuments));
        if (Active == document)
        {
            Active = null;
            if (_recent.Count > 0)
                Activate(_recent[0]);
            else
                Sync.Restore(default, default, 0, Selection.None, false);
        }

        await document.DisposeAsync().ConfigureAwait(true);
        return true;
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

    private void Add(DocumentViewModel document)
    {
        document.DataChanged += OnDocumentChanged;
        Documents.Add(document);
        OnPropertyChanged(nameof(HasDocuments));
        Activate(document);
    }

    private void OnDocumentChanged(DocumentViewModel document)
    {
        if (document != Active)
            return;

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
