using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using NexNet.Inspector.App.Controls.Compare;
using NexNet.Inspector.App.Controls.Primitives;
using NexNet.Inspector.App.Model;
using NexNet.Inspector.App.Services;
using NexNet.Inspector.Core.Assembly;
using NexNet.Inspector.Core.Compare;
using NexNet.Inspector.Core.Model;
using NexNet.Inspector.Core.Session;

namespace NexNet.Inspector.App.ViewModels;

/// <summary>
/// The Compare workspace (screens/compare, SYSTEM.md flow 5): the two documents ticked in the tabs (OQ11) compared by
/// the Core engine, aligned Relative / Wall-clock / On event; the Entities sidebar (the union by category and group,
/// Δ markers, search, the checkboxes that give groups their rows), the aligned rows (<see cref="Rows"/>, Heat | Packed,
/// Only differences), and the selected entity as <see cref="Detail"/> for the field diff and A ⇄ B hex below (I32). A
/// double-click on a region opens it in Inspect on that document, its entity selected.
/// </summary>
/// <remarks>
/// <para>
/// The compare is built when the workspace shows with two ticked documents, and kept while the same two sessions are
/// compared; a document still growing (indexing, live) refreshes it at most once a second, and once more when it
/// settles.
/// </para>
/// <para>
/// Building, refreshing, re-aligning and the selected entity's field diff run off the UI thread (a recording of hundreds
/// of MB would otherwise freeze the window): each reads the sessions under their gates and its result is published here
/// through the UI dispatcher. The latest request wins: a newer one cancels the work it supersedes, whose result is then
/// dropped. A first build shows "Comparing…"; a refresh or re-alignment keeps the previous result on screen until the
/// new one lands. Published compares are never changed off the UI thread (a refresh builds a new one; an alignment is
/// prepared aside and swapped in), so the views read them freely.
/// </para>
/// </remarks>
public sealed partial class CompareWorkspaceViewModel : ObservableObject, IDisposable
{
    /// <summary>The empty state while the first compare of a pair builds.</summary>
    public const string ComparingText = "Comparing…";

    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(1);

    private static readonly CompareCategory[] SidebarCategories =
        [CompareCategory.Invocations, CompareCategory.Pipes, CompareCategory.Collections, CompareCategory.Control, CompareCategory.Handshake];

    private readonly DocumentsViewModel _documents;
    private readonly ShellChromeViewModel _chrome;
    private readonly bool[] _sidebarExpanded = new bool[(int)CompareCategory.Handshake + 1];
    private readonly Action<DocumentViewModel> _onDocumentData;
    private readonly Action<CompareEntityItem> _onShownChanged;
    private DocumentViewModel? _a;
    private DocumentViewModel? _b;
    private long _lastBuild;
    private bool _selecting;
    private CompareSession? _shownSession;
    private int _shownMatch = -1;

    // The work in flight (null when none) and the tasks that finish once its result is published or dropped.
    private CancellationTokenSource? _build;
    private CaptureSession? _buildA;
    private CaptureSession? _buildB;
    private CancellationTokenSource? _align;
    private CancellationTokenSource? _detail;
    private volatile Task _buildDone = Task.CompletedTask;
    private volatile Task _alignDone = Task.CompletedTask;
    private volatile Task _detailDone = Task.CompletedTask;
    private string? _failure;

    // "Open in Compare" picks the entity selected in Inspect once the compare it opens has landed.
    private DocumentViewModel? _pendingFrom;
    private Selection _pendingSelection;

    /// <param name="documents">The open documents (the tabs' Compare checkboxes pick the pair).</param>
    /// <param name="chrome">The shell: the Inspect | Compare switch.</param>
    public CompareWorkspaceViewModel(DocumentsViewModel documents, ShellChromeViewModel chrome)
    {
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentNullException.ThrowIfNull(chrome);
        _documents = documents;
        _chrome = chrome;
        _onDocumentData = OnDocumentData;
        _onShownChanged = item => Rows.SetGroupShown(item.Group, item.IsShown);
        _sidebarExpanded[(int)CompareCategory.Invocations] = true;
        _sidebarExpanded[(int)CompareCategory.Pipes] = true;

        Align = new EnumChoice<Alignment>(
            [(Alignment.Relative, "Relative"), (Alignment.WallClock, "Wall-clock"), (Alignment.OnEvent, "On event · Session ready")],
            Alignment.OnEvent);
        Align.ValueChanged += (_, mode) => Realign(mode);
        RowMode = new EnumChoice<CompareRowMode>([(CompareRowMode.Heat, "Heat"), (CompareRowMode.Packed, "Packed")], CompareRowMode.Heat);
        RowMode.ValueChanged += (_, mode) => Rows.RowMode = mode;

        Rows.SelectRequested += Select;
        Rows.OpenRequested += OpenInInspect;
        Rows.Changed += changes =>
        {
            if ((changes & CompareChanges.Window) != 0) UpdateSubtitle();
        };

        documents.PropertyChanged += OnDocumentsChanged;
        documents.Documents.CollectionChanged += OnTabsChanged;
        chrome.PropertyChanged += OnChromeChanged;
        // The title bar's range readout reads this window while Compare shows.
        chrome.TimeWindow.FollowCompare(Rows, () => chrome.IsCompare);
        UpdateEmptyText();
    }

    /// <summary>The aligned rows' state, drawn by <see cref="CompareRowsControl"/>.</summary>
    public CompareRowsModel Rows { get; } = new();

    /// <summary>Align: Relative | Wall-clock | On event · Session ready (re-offsets file B).</summary>
    public EnumChoice<Alignment> Align { get; }

    /// <summary>Rows: Heat | Packed (how collapsed categories draw).</summary>
    public EnumChoice<CompareRowMode> RowMode { get; }

    /// <summary>The Entities sidebar's rows.</summary>
    public ObservableCollection<CompareEntityItem> Entities { get; } = [];

    /// <summary>"Only differences": hides the rows whose entities match.</summary>
    [ObservableProperty]
    public partial bool OnlyDifferences { get; set; }

    /// <summary>The sidebar's search: groups whose label contains it (any case).</summary>
    [ObservableProperty]
    public partial string SearchText { get; set; } = "";

    /// <summary>The sidebar's selected row (the pinned entity).</summary>
    [ObservableProperty]
    public partial CompareEntityItem? SelectedEntity { get; set; }

    /// <summary>The compare; null until two documents with sessions are ticked.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSession))]
    public partial CompareSession? Session { get; private set; }

    /// <summary>True when a compare shows (otherwise the empty state does).</summary>
    public bool HasSession => Session is not null;

    /// <summary>True while a compare is built, refreshed or re-aligned off the UI thread.</summary>
    [ObservableProperty]
    public partial bool IsComputing { get; private set; }

    /// <summary>
    /// Test hook: runs on the worker before each piece of background work, with the token that cancels it (a test slows
    /// or blocks the work here to prove the UI thread stays free and that superseded work is cancelled).
    /// </summary>
    internal Action<CompareWork, CancellationToken>? WorkHook { get; set; }

    /// <summary>
    /// Completes once no background work is pending: the compare, its alignment and the selected entity's field diff
    /// are published. For tests and the capture scenarios, which read the results synchronously.
    /// </summary>
    public async Task WhenComputedAsync()
    {
        while (true)
        {
            Task build = _buildDone, align = _alignDone, detail = _detailDone;
            await Task.WhenAll(build, align, detail).ConfigureAwait(false);
            // A published build may have started a re-alignment or a field diff: wait for those too.
            if (build == _buildDone && align == _alignDone && detail == _detailDone) return;
        }
    }

    /// <summary>
    /// The selected entity, for the field diff and A ⇄ B hex below the rows (I32 fills them from
    /// <see cref="CompareSession.GetFieldDiff"/>); null when none is selected.
    /// </summary>
    [ObservableProperty]
    public partial EntityMatch? Detail { get; private set; }

    /// <summary>
    /// The bottom panel (I32): the field diff and A ⇄ B hex of <see cref="Detail"/>, re-shown when the selected entity or
    /// the compare changes (not on a re-alignment, which leaves field content alone and keeps the picked field).
    /// </summary>
    public CompareDetailViewModel DetailPanel { get; } = new();

    /// <summary>The splitters' sizes (the sidebar's width, the bottom panel's height); the window shares one set with Inspect.</summary>
    [ObservableProperty]
    public partial PaneSizesViewModel Sizes { get; set; } = new(null);

    /// <summary>The bottom panel's subtitle: "Invocation #812 SendAttachment".</summary>
    [ObservableProperty]
    public partial string DetailSubtitle { get; private set; } = "";

    /// <summary>The aligned view's subtitle: "whole session · 42.1 s".</summary>
    [ObservableProperty]
    public partial string Subtitle { get; private set; } = "";

    /// <summary>Differences in the Δ list (the strip's badge).</summary>
    [ObservableProperty]
    public partial int DeltaCount { get; private set; }

    /// <summary>What the empty workspace says to do next.</summary>
    [ObservableProperty]
    public partial string EmptyText { get; private set; } = "";

    /// <summary>The Entities header's subtitle.</summary>
    public string EntitiesSubtitle => "union of " + CompareSession.DocumentCount.ToString(CultureInfo.InvariantCulture) + " files";

    /// <summary>Document A (the first ticked tab).</summary>
    public DocumentViewModel? DocumentA => _a;

    /// <summary>Document B.</summary>
    public DocumentViewModel? DocumentB => _b;

    /// <summary>True when the workspace shows: documents are open and Compare is the workspace.</summary>
    public bool IsShown => _documents.HasDocuments && _chrome.IsCompare;

    /// <summary>
    /// Shows Compare (the command "Open in Compare"): when fewer than two documents are ticked, the active one and then
    /// the next tabs are ticked to make the pair.
    /// </summary>
    public void Show()
    {
        // The entity selected in Inspect (or picked in the palette's "Open invocation #812 in Compare") opens selected.
        DocumentViewModel? from = _documents.Active;
        Selection selection = _documents.Sync.Selection;
        if (_documents.ComparedCount < DocumentsViewModel.MaxCompared && _documents.Active is { IsIncludedInCompare: false } active)
            active.IsIncludedInCompare = true;
        foreach (DocumentViewModel document in _documents.Documents)
        {
            if (_documents.ComparedCount >= DocumentsViewModel.MaxCompared) break;
            document.IsIncludedInCompare = true;
        }

        _pendingFrom = from;
        _pendingSelection = selection;
        _chrome.Workspace.Value = Workspace.Compare;
        Refresh(force: true);
        if (_build is null) SelectPending();
    }

    private void SelectPending()
    {
        DocumentViewModel? from = _pendingFrom;
        _pendingFrom = null;
        if (Session is { } session && from is not null && MatchOf(session, from, _pendingSelection) is var match and >= 0) Select(match);
    }

    /// <summary>The compare row of an entity selected in one of the two documents; -1 when it has none.</summary>
    private int MatchOf(CompareSession session, DocumentViewModel document, in Selection selection)
    {
        if (selection.Kind != SelectionKind.Entity) return -1;
        CompareSide side;
        if (ReferenceEquals(document, _a)) side = CompareSide.A;
        else if (ReferenceEquals(document, _b)) side = CompareSide.B;
        else return -1;

        CompareEntityKind kind;
        switch (selection.Entity)
        {
            case EntityKind.Invocation:
                kind = CompareEntityKind.Invocation;
                break;
            case EntityKind.Pipe:
                kind = CompareEntityKind.Pipe;
                break;
            case EntityKind.Greeting:
                kind = CompareEntityKind.Handshake;
                break;
            default:
                return -1;
        }

        ReadOnlySpan<EntityMatch> matches = session.Matches;
        for (int i = 0; i < matches.Length; i++)
        {
            ref readonly EntityMatch match = ref matches[i];
            if (match.Key.Kind != kind || (kind == CompareEntityKind.Handshake && match.Key.Id == (ushort)HandshakeItem.Preamble)) continue;
            EntitySide entity = match.Get(side);
            if (entity.IsPresent && entity.Index == selection.EntityIndex) return i;
        }

        return -1;
    }

    /// <summary>
    /// The open-recording dialog's "Compare two sessions…": opens the recording's first two sessions as documents
    /// (from its path; the dialog disposes its own recording), ticks just them and shows Compare.
    /// </summary>
    /// <returns>True when Compare took over.</returns>
    public async Task<bool> CompareRecordingAsync(RecordingFile recording)
    {
        ArgumentNullException.ThrowIfNull(recording);
        if (recording.Path is not { } path || recording.Sessions.Count < DocumentsViewModel.MaxCompared) return false;
        long first = recording.Sessions[0].SessionId, second = recording.Sessions[1].SessionId;
        FileDocumentViewModel? a = await _documents.OpenRecordingAsync(path, new RecordingOpenChoice(first, SchemaChoice.Embedded)).ConfigureAwait(true);
        FileDocumentViewModel? b = await _documents.OpenRecordingAsync(path, new RecordingOpenChoice(second, SchemaChoice.Embedded)).ConfigureAwait(true);
        if (a is null || b is null) return false;

        foreach (DocumentViewModel document in _documents.Documents.ToArray())
        {
            if (document != a && document != b) document.IsIncludedInCompare = false;
        }

        a.IsIncludedInCompare = true;
        b.IsIncludedInCompare = true;
        _chrome.Workspace.Value = Workspace.Compare;
        Refresh(force: true);
        return true;
    }

    /// <summary>Selects (pins) an entity and shows its detail; -1 clears.</summary>
    public void Select(int match)
    {
        Rows.Select(match);
        if (Session is { } session && Rows.Selected >= 0)
        {
            EntityMatch row = session.Matches[Rows.Selected];
            Detail = row;
            DetailSubtitle = KindName(row.Key.Kind) + CompareRowsModel.EntityTitle(session, row.Index, row.A.IsPresent ? CompareSide.A : CompareSide.B);
        }
        else
        {
            Detail = null;
            DetailSubtitle = "";
        }

        ShowDetail();
        RebuildEntities();
    }

    private void ShowDetail()
    {
        CompareSession? session = Detail is null ? null : Session;
        int match = Detail?.Index ?? -1;
        if (ReferenceEquals(session, _shownSession) && match == _shownMatch)
            return;
        _shownSession = session;
        _shownMatch = match;
        Cancel(ref _detail);
        if (session is null || match < 0)
        {
            DetailPanel.Show(null, -1, DetailSubtitle);
            return;
        }

        // A big entity's decode (a pipe of many items) must not hold the UI: the panel keeps what it shows until the
        // diff lands.
        string title = DetailSubtitle;
        CancellationTokenSource work = new();
        _detail = work;
        _detailDone = Run(CompareWork.FieldDiff, work, _ => session.GetFieldDiff(match), diff =>
        {
            _detail = null;
            DetailPanel.Show(session, match, title, diff);
        });
    }

    /// <summary>
    /// Opens a place of the compare in Inspect: activates that file's document, switches the workspace and shows the
    /// entity selected (or the time under the playhead) in a window around it. An entity only the other file holds
    /// opens there.
    /// </summary>
    public void OpenInInspect(CompareOpenRequest request)
    {
        if (Session is not { } session) return;
        CompareSide side = request.Side;
        EntitySide entity = EntitySide.Absent;
        if ((uint)request.Match < (uint)session.Matches.Length)
        {
            EntityMatch match = session.Matches[request.Match];
            if (!match.Get(side).IsPresent) side = side == CompareSide.A ? CompareSide.B : CompareSide.A;
            entity = match.Get(side);
        }

        if ((side == CompareSide.A ? _a : _b) is not { } document || !_documents.Documents.Contains(document)) return;
        CaptureSession capture = session.Get(side);
        long frequency = Math.Max(1, capture.Header.StopwatchFrequency);
        long start = entity.IsPresent && entity.Frames > 0 ? entity.StartTicks : session.Clock.ToTicks(side, request.Time);
        long end = entity.IsPresent && entity.Frames > 0 ? entity.EndTicks : start;
        // The entity a third of the window, at least 20 ms either side, so its neighbours show.
        long pad = Math.Max(end - start, frequency / 50);

        _documents.Activate(document);
        _chrome.Workspace.Value = Workspace.Inspect;
        _documents.Sync.SetWindow(new TimeWindow(start - pad, end + pad));
        Selection selection = entity.IsPresent ? SelectionOf(session, request.Match, side, entity) : Selection.None;
        if (selection.IsNone) _documents.Sync.SetPlayhead(start);
        else _documents.Sync.Select(selection, start);
    }

    /// <summary>Rebuilds or refreshes the compare from the ticked documents, off the UI thread.</summary>
    /// <param name="force">Refresh a stale compare now, however recently it was built.</param>
    public void Refresh(bool force = false)
    {
        DocumentViewModel[] pair = _documents.Compared.ToArray();
        DocumentViewModel? a = pair.Length == CompareSession.DocumentCount ? pair[0] : null;
        DocumentViewModel? b = pair.Length == CompareSession.DocumentCount ? pair[1] : null;
        Track(a, b);
        try
        {
            if (!_chrome.IsCompare && Session is null && _build is null) return;

            if (a?.Session is not { } sessionA || b?.Session is not { } sessionB)
            {
                CancelWork();
                _pendingFrom = null;
                SetSession(null, null, null, keep: false);
                return;
            }

            // The build in flight is already for this pair; whether its result is stale is checked when it lands.
            if (_build is not null && _buildA == sessionA && _buildB == sessionB) return;

            if (Session is { } current && current.A == sessionA && current.B == sessionB)
            {
                // A build of another pair (ticked and unticked meanwhile) is superseded by the pair on screen.
                CancelBuild();
                if (!current.IsStale) return;
                long now = _documents.Time.GetTimestamp();
                bool settled = a.CaptureState != CaptureState.Open && b.CaptureState != CaptureState.Open;
                if (!force && !settled && _documents.Time.GetElapsedTime(_lastBuild, now) < RefreshInterval) return;
                StartBuild(sessionA, sessionB, CompareWork.Refresh);
                return;
            }

            // Another pair: its rows would mislead while the new compare builds, so the workspace says "Comparing…".
            CancelWork();
            if (Session is not null) SetSession(null, null, null, keep: false);
            StartBuild(sessionA, sessionB, CompareWork.Build);
        }
        finally
        {
            UpdateComputing();
        }
    }

    private void StartBuild(CaptureSession a, CaptureSession b, CompareWork kind)
    {
        CancelBuild();
        _lastBuild = _documents.Time.GetTimestamp();
        _failure = null;
        Alignment mode = Align.Value;
        CancellationTokenSource work = new();
        _build = work;
        _buildA = a;
        _buildB = b;
        _buildDone = Run(kind, work, token => Build(a, b, mode, token), built => OnBuilt(built, keep: kind == CompareWork.Refresh));
    }

    /// <summary>The worker's part of a build: the union, its alignment and both documents' heat-row densities.</summary>
    private static BuiltCompare Build(CaptureSession a, CaptureSession b, Alignment mode, CancellationToken token)
    {
        CompareSession session = new(a, b, null, token);
        if (mode != Alignment.Relative) session.ApplyAlignment(session.PrepareAlignment(mode, AlignmentEvent.SessionReady, token));
        token.ThrowIfCancellationRequested();
        CompareDensity densityA = CompareDensity.Build(a);
        token.ThrowIfCancellationRequested();
        return new BuiltCompare(session, densityA, CompareDensity.Build(b));
    }

    private void OnBuilt(BuiltCompare built, bool keep)
    {
        // An alignment prepared for the compare being replaced does not apply to the new one.
        Cancel(ref _align);
        EntityKey? selected = keep && Session is { } previous && (uint)Rows.Selected < (uint)previous.Matches.Length
            ? previous.Matches[Rows.Selected].Key
            : null;
        SetSession(built.Session, built.DensityA, built.DensityB, keep, selected);
        // The build counts as in flight until its pending selection is made, so "Open in Compare" makes it here only.
        SelectPending();
        _build = null;
        _buildA = null;
        _buildB = null;
        // Align changed while it built.
        if (built.Session.Alignment != Align.Value) Realign(Align.Value);
        UpdateComputing();
        // Data that arrived while it built (or the capture settling meanwhile) refreshes it again, coalesced as ever.
        if (built.Session.IsStale && _chrome.IsCompare) Refresh();
    }

    private void SetSession(CompareSession? session, CompareDensity? densityA, CompareDensity? densityB, bool keep, EntityKey? selected = null)
    {
        Rows.SetSession(session, densityA, densityB, keep);
        // The scope's compare time depends on this compare's clock.
        ApplyScope();
        Session = session;
        // A refreshed compare rebuilt its union (and dropped its field diffs): the panel shows the entity again.
        _shownMatch = int.MinValue;
        UpdateFiles();
        DeltaCount = session?.Deltas.Count ?? 0;
        int match = session is not null && selected is { } key ? session.FindMatch(key) : -1;
        if (!keep || match < 0) match = -1;
        Select(match);
        UpdateSubtitle();
        UpdateEmptyText();
    }

    private void Realign(Alignment mode)
    {
        // A build in flight aligns as it lands.
        if (Session is not { } session) return;
        Cancel(ref _align);
        CancellationTokenSource work = new();
        _align = work;
        _alignDone = Run(CompareWork.Align, work, token => session.PrepareAlignment(mode, AlignmentEvent.SessionReady, token), prepared =>
        {
            _align = null;
            if (ReferenceEquals(session, Session)) OnAligned(session, prepared);
            UpdateComputing();
        });
        UpdateComputing();
    }

    private void OnAligned(CompareSession session, CompareAlignment prepared)
    {
        session.ApplyAlignment(prepared);
        Rows.OnAligned();
        ApplyScope();
        DeltaCount = session.Deltas.Count;
        UpdateFiles();
        if (Rows.Selected >= 0) Detail = session.Matches[Rows.Selected];
        RebuildEntities();
        UpdateSubtitle();
    }

    /// <summary>
    /// Runs <paramref name="compute"/> on the thread pool and publishes its result through the UI dispatcher, unless
    /// <paramref name="work"/> was cancelled first (superseded): then the result is dropped.
    /// </summary>
    /// <returns>A task that completes once the result is published or dropped.</returns>
    private Task Run<T>(CompareWork kind, CancellationTokenSource work, Func<CancellationToken, T> compute, Action<T> publish)
    {
        TaskCompletionSource done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken token = work.Token;
        IUiDispatcher ui = _documents.Ui;
        Action<CompareWork, CancellationToken>? hook = WorkHook;
        _ = Task.Run(() =>
        {
            T result;
            try
            {
                hook?.Invoke(kind, token);
                token.ThrowIfCancellationRequested();
                result = compute(token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                done.TrySetResult();
                return;
            }
            catch (Exception e)
            {
                ui.Post(() =>
                {
                    // Superseded work's failure is as irrelevant as its result would have been.
                    if (token.IsCancellationRequested)
                    {
                        done.TrySetResult();
                        return;
                    }

                    OnFailed(kind, e);
                    done.TrySetException(e);
                });
                return;
            }

            ui.Post(() =>
            {
                // Cancelled on the UI thread too, so a request made before this runs always wins.
                if (token.IsCancellationRequested)
                {
                    done.TrySetResult();
                    return;
                }

                try
                {
                    publish(result);
                    done.TrySetResult();
                }
                catch (Exception e)
                {
                    done.TrySetException(e);
                    throw;
                }
                finally
                {
                    work.Dispose();
                }
            });
        });
        return done.Task;
    }

    private void OnFailed(CompareWork kind, Exception e)
    {
        switch (kind)
        {
            case CompareWork.Build:
            case CompareWork.Refresh:
                _build = null;
                _buildA = null;
                _buildB = null;
                _failure = "Could not compare these documents: " + e.Message;
                SetSession(null, null, null, keep: false);
                break;
            case CompareWork.Align:
                _align = null;
                break;
            default:
                _detail = null;
                DetailPanel.Show(null, -1);
                break;
        }

        UpdateComputing();
    }

    private void CancelBuild()
    {
        Cancel(ref _build);
        _buildA = null;
        _buildB = null;
    }

    private void CancelWork()
    {
        CancelBuild();
        Cancel(ref _align);
    }

    // The worker may still hold the token, so the source is left to the collector rather than disposed here.
    private static void Cancel(ref CancellationTokenSource? work)
    {
        work?.Cancel();
        work = null;
    }

    private void UpdateComputing()
    {
        IsComputing = _build is not null || _align is not null;
        UpdateEmptyText();
    }

    private void UpdateFiles()
    {
        if (Session is not { } session || _a is null || _b is null)
        {
            Rows.SetFiles("", "", ThemeBrush.DirectionInbound, "", "", ThemeBrush.DirectionOutbound);
            return;
        }

        DetailPanel.SetFiles(_a.Title, _b.Title);

        CompareClock clock = session.Clock;
        Rows.SetFiles(_a.Title, FileDetail(_a, session.A, 0), BrushOf(_a.Marker),
            _b.Title, FileDetail(_b, session.B, clock.OffsetB - clock.OffsetA), BrushOf(_b.Marker));
    }

    // "server side · recorded 2026-11-20 14:02 · 3 sessions in file · offset −0.412 ms".
    private string FileDetail(DocumentViewModel document, CaptureSession session, long offset)
    {
        string side = session.Role == Core.Format.NntlSessionRole.Client ? "client side" : "server side";
        DateTime utc = new(Math.Clamp(session.Header.WallClockUtcTicks, DateTime.MinValue.Ticks, DateTime.MaxValue.Ticks), DateTimeKind.Utc);
        DateTime local = TimeZoneInfo.ConvertTimeFromUtc(utc, _documents.Time.LocalTimeZone);
        string detail = side + " · recorded " + local.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        if (document is FileDocumentViewModel { Recording.Sessions.Count: > 1 } file)
            detail += " · " + file.Recording.Sessions.Count.ToString(CultureInfo.InvariantCulture) + " sessions in file";
        return detail + " · offset " + CompareFormat.Offset(offset / (double)TimeSpan.TicksPerMillisecond);
    }

    /// <summary>A file's colour in Compare: its tab marker's.</summary>
    public static ThemeBrush BrushOf(TabMarker marker) => marker switch
    {
        TabMarker.FileOutbound => ThemeBrush.DirectionOutbound,
        TabMarker.Live => ThemeBrush.StatusRecording,
        _ => ThemeBrush.DirectionInbound,
    };

    private static string KindName(CompareEntityKind kind) => kind switch
    {
        CompareEntityKind.Invocation => "Invocation ",
        _ => "",
    };

    private void UpdateSubtitle()
    {
        if (Session is not { } session)
        {
            Subtitle = "";
            return;
        }

        TimeWindow range = session.Range;
        TimeWindow window = Rows.Window;
        Subtitle = window.IsEmpty || (window.Start <= range.Start && window.End >= range.End)
            ? "whole session · " + CompareFormat.Span(range.Duration)
            : CompareFormat.Span(window.Duration) + " of " + CompareFormat.Span(range.Duration);
    }

    private void UpdateEmptyText()
    {
        int ticked = _documents.ComparedCount;
        EmptyText = ticked switch
        {
            0 => "Tick the checkboxes of two document tabs to compare them.",
            1 => "Tick one more document tab to compare it with the first.",
            _ when _build is not null => ComparingText,
            _ when _failure is not null => _failure,
            _ => "Waiting for both documents' data…",
        };
    }

    private static Selection SelectionOf(CompareSession session, int match, CompareSide side, in EntitySide entity)
    {
        CompareEntityKind kind = session.Matches[match].Key.Kind;
        CaptureSession capture = session.Get(side);
        switch (kind)
        {
            case CompareEntityKind.Invocation:
                return Selection.OfEntity(EntityKind.Invocation, entity.Index);
            case CompareEntityKind.Pipe:
                return Selection.OfEntity(EntityKind.Pipe, entity.Index);
            case CompareEntityKind.Collection:
                lock (capture.Gate)
                {
                    return (uint)entity.Index < (uint)capture.Assembler.Collections.Length
                        ? Selection.OfEntity(EntityKind.Pipe, capture.Assembler.Collections[entity.Index].Pipe)
                        : Selection.None;
                }

            case CompareEntityKind.Handshake when session.Matches[match].Key.Id != (ushort)HandshakeItem.Preamble:
                return Selection.OfEntity(EntityKind.Greeting, entity.Index);
            default:
                return Selection.None;
        }
    }

    private void RebuildEntities()
    {
        _selecting = true;
        try
        {
            Entities.Clear();
            SelectedEntity = null;
            if (Session is not { } session) return;

            string search = SearchText.Trim();
            ReadOnlySpan<EntityGroupRow> groups = session.Groups;
            int selected = Rows.Selected;
            CompareCategory pinned = selected >= 0 ? CompareRowsModel.CategoryOf(session.Matches[selected].Key.Kind) : CompareCategory.Transport;
            foreach (CompareCategory category in SidebarCategories)
            {
                (int first, int count) = Rows.GroupsOf(category);
                if (count == 0) continue;
                bool categoryMatches = search.Length == 0 || CompareRowsModel.CategoryLabel(category).Contains(search, StringComparison.OrdinalIgnoreCase);
                List<CompareEntityItem> children = [];
                for (int g = first; g < first + count; g++)
                {
                    string label = SidebarLabel(session, g);
                    if (!categoryMatches && !label.Contains(search, StringComparison.OrdinalIgnoreCase)) continue;
                    EntityGroupRow group = groups[g];
                    bool showsFrames = group.Count == 1 || group.Key.Kind != CompareEntityKind.Invocation;
                    children.Add(new CompareEntityItem(CompareEntityItemKind.Group, category, g, -1, label,
                        showsFrames ? CompareFormat.Compact(Frames(session, group)) : "", group.Differing > 0, isShown: Rows.IsGroupShown(g),
                        shownChanged: _onShownChanged));
                }

                if (children.Count == 0 && !categoryMatches) continue;
                // A search opens every category it finds something in.
                bool expanded = _sidebarExpanded[(int)category] || search.Length > 0;
                Entities.Add(new CompareEntityItem(CompareEntityItemKind.Category, category, -1, -1, CompareRowsModel.CategoryLabel(category),
                    CategoryCount(session, category, first, count), Rows.CategoryDiffers(category), isExpanded: expanded));
                if (!expanded) continue;
                if (pinned == category && selected >= 0)
                {
                    EntityMatch match = session.Matches[selected];
                    CompareSide side = match.A.IsPresent ? CompareSide.A : CompareSide.B;
                    CompareEntityItem item = new(CompareEntityItemKind.Pinned, category, -1, selected, CompareRowsModel.EntityTitle(session, selected, side),
                        match.Get(side).Frames.ToString(CultureInfo.InvariantCulture) + " fr", match.HasDifferences);
                    Entities.Add(item);
                    SelectedEntity = item;
                }

                foreach (CompareEntityItem child in children) Entities.Add(child);
            }
        }
        finally
        {
            _selecting = false;
        }
    }

    private string SidebarLabel(CompareSession session, int group)
    {
        EntityGroupRow row = session.Groups[group];
        return row.Count == 1 ? Rows.GroupLabel(group, row.Key.Kind == CompareEntityKind.Invocation || session.Matches[row.FirstMatch].A.IsPresent ? CompareSide.A : CompareSide.B)
            : session.GetLabel(row.FirstMatch) + " ×" + CompareFormat.Count(row.Count);
    }

    private static long Frames(CompareSession session, in EntityGroupRow group)
    {
        long a = 0, b = 0;
        foreach (ref readonly EntityMatch match in session.Matches.Slice(group.FirstMatch, group.Count))
        {
            a += match.A.Frames;
            b += match.B.Frames;
        }

        return Math.Max(a, b);
    }

    private static string CategoryCount(CompareSession session, CompareCategory category, int first, int count)
    {
        ReadOnlySpan<EntityGroupRow> groups = session.Groups;
        if (category != CompareCategory.Control)
        {
            long entities = 0;
            for (int g = first; g < first + count; g++) entities += groups[g].Count;
            return CompareFormat.Count(entities);
        }

        long frames = 0;
        for (int g = first; g < first + count; g++) frames += Frames(session, groups[g]);
        return CompareFormat.Compact(frames);
    }

    partial void OnSelectedEntityChanged(CompareEntityItem? value)
    {
        if (_selecting || value is null) return;
        switch (value.Kind)
        {
            case CompareEntityItemKind.Category:
                _sidebarExpanded[(int)value.Category] = !value.IsExpanded;
                // The list is rebuilt after the selection change finishes, not inside the list's own handler.
                _documents.Ui.Post(RebuildEntities);
                break;
            case CompareEntityItemKind.Group when Session is { } session:
                int match = CompareRowsControl.FirstDiffering(session, session.Groups[value.Group]);
                _documents.Ui.Post(() => Select(match));
                break;
        }
    }

    partial void OnOnlyDifferencesChanged(bool value) => Rows.OnlyDifferences = value;

    partial void OnSearchTextChanged(string value) => RebuildEntities();

    private void Track(DocumentViewModel? a, DocumentViewModel? b)
    {
        if (a == _a && b == _b) return;
        if (_a is not null)
        {
            _a.DataChanged -= _onDocumentData;
            _a.ScopeChanged -= OnScopeChanged;
        }

        if (_b is not null)
        {
            _b.DataChanged -= _onDocumentData;
            _b.ScopeChanged -= OnScopeChanged;
        }

        _a = a;
        _b = b;
        if (_a is not null)
        {
            _a.DataChanged += _onDocumentData;
            _a.ScopeChanged += OnScopeChanged;
        }

        if (_b is not null)
        {
            _b.DataChanged += _onDocumentData;
            _b.ScopeChanged += OnScopeChanged;
        }
        OnPropertyChanged(nameof(DocumentA));
        OnPropertyChanged(nameof(DocumentB));
    }

    private void OnScopeChanged(DocumentViewModel document, ScopeChange change) => ApplyScope();

    /// <summary>
    /// The compared documents' scope on the aligned axis (D2): file A's when it is scoped, else file B's, converted to
    /// compare time through that file's clock. Empty when neither is scoped.
    /// </summary>
    public TimeWindow ScopeOnAxis()
    {
        if (Session is not { } session) return default;
        (CompareSide side, TimeScope? scope) = _a?.Scope is { } a ? (CompareSide.A, (TimeScope?)a) : (CompareSide.B, _b?.Scope);
        if (scope is not { } s) return default;
        long start = session.Clock.ToCompare(side, s.Start);
        long end = session.Clock.ToCompare(side, s.End);
        return end > start ? new TimeWindow(start, end) : default;
    }

    private void ApplyScope()
    {
        Rows.SetScope(ScopeOnAxis());
        UpdateSubtitle();
    }

    private void OnDocumentData(DocumentViewModel document)
    {
        if (_chrome.IsCompare) Refresh();
    }

    private void OnDocumentsChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(DocumentsViewModel.HasDocuments):
                OnPropertyChanged(nameof(IsShown));
                break;
            case nameof(DocumentsViewModel.ComparedCount):
                Refresh();
                break;
        }
    }

    private void OnTabsChanged(object? sender, NotifyCollectionChangedEventArgs e) => Refresh();

    private void OnChromeChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ShellChromeViewModel.IsCompare)) return;
        OnPropertyChanged(nameof(IsShown));
        if (_chrome.IsCompare) Refresh(force: true);
    }

    public void Dispose()
    {
        CancelWork();
        Cancel(ref _detail);
        _documents.PropertyChanged -= OnDocumentsChanged;
        _documents.Documents.CollectionChanged -= OnTabsChanged;
        _chrome.PropertyChanged -= OnChromeChanged;
        Track(null, null);
    }
}

/// <summary>The kinds of work the Compare workspace runs off the UI thread.</summary>
internal enum CompareWork
{
    /// <summary>A new pair's compare.</summary>
    Build,

    /// <summary>The same pair rebuilt after its documents grew.</summary>
    Refresh,

    /// <summary>A new alignment of the compare shown.</summary>
    Align,

    /// <summary>The selected entity's field diff.</summary>
    FieldDiff,
}

/// <summary>A compare built off the UI thread with its documents' heat-row densities.</summary>
internal readonly record struct BuiltCompare(CompareSession Session, CompareDensity DensityA, CompareDensity DensityB);
