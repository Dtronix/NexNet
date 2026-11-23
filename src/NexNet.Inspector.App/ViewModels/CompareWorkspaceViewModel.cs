using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using NexNet.Inspector.App.Controls.Compare;
using NexNet.Inspector.App.Controls.Primitives;
using NexNet.Inspector.App.Model;
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
/// The compare is built when the workspace shows with two ticked documents, and kept while the same two sessions are
/// compared; a document still growing (indexing, live) refreshes it at most once a second, and once more when it
/// settles.
/// </remarks>
public sealed partial class CompareWorkspaceViewModel : ObservableObject, IDisposable
{
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
        if (_documents.ComparedCount < DocumentsViewModel.MaxCompared && _documents.Active is { IsIncludedInCompare: false } active)
            active.IsIncludedInCompare = true;
        foreach (DocumentViewModel document in _documents.Documents)
        {
            if (_documents.ComparedCount >= DocumentsViewModel.MaxCompared) break;
            document.IsIncludedInCompare = true;
        }

        _chrome.Workspace.Value = Workspace.Compare;
        Refresh(force: true);
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
        DetailPanel.Show(session, match, DetailSubtitle);
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

    /// <summary>Rebuilds or refreshes the compare from the ticked documents.</summary>
    /// <param name="force">Refresh a stale compare now, however recently it was built.</param>
    public void Refresh(bool force = false)
    {
        DocumentViewModel[] pair = _documents.Compared.ToArray();
        DocumentViewModel? a = pair.Length == CompareSession.DocumentCount ? pair[0] : null;
        DocumentViewModel? b = pair.Length == CompareSession.DocumentCount ? pair[1] : null;
        Track(a, b);
        UpdateEmptyText();
        if (!_chrome.IsCompare && Session is null) return;

        if (a?.Session is not { } sessionA || b?.Session is not { } sessionB)
        {
            SetSession(null, keep: false);
            return;
        }

        if (Session is { } current && current.A == sessionA && current.B == sessionB)
        {
            if (!current.IsStale) return;
            long now = _documents.Time.GetTimestamp();
            bool settled = a.CaptureState != CaptureState.Open && b.CaptureState != CaptureState.Open;
            if (!force && !settled && _documents.Time.GetElapsedTime(_lastBuild, now) < RefreshInterval) return;
            EntityKey? selected = Rows.Selected >= 0 ? current.Matches[Rows.Selected].Key : null;
            current.Refresh();
            SetSession(current, keep: true, selected);
            return;
        }

        CompareSession session = new(sessionA, sessionB);
        if (Align.Value != Alignment.Relative) session.Align(Align.Value);
        SetSession(session, keep: false);
    }

    private void SetSession(CompareSession? session, bool keep, EntityKey? selected = null)
    {
        _lastBuild = _documents.Time.GetTimestamp();
        CompareDensity? densityA = session is null ? null : CompareDensity.Build(session.A);
        CompareDensity? densityB = session is null ? null : CompareDensity.Build(session.B);
        Rows.SetSession(session, densityA, densityB, keep);
        Session = session;
        // A refreshed compare rebuilt its union (and dropped its field diffs): the panel shows the entity again.
        _shownMatch = int.MinValue;
        UpdateFiles();
        DeltaCount = session?.Deltas.Count ?? 0;
        int match = session is not null && selected is { } key ? session.FindMatch(key) : -1;
        if (!keep || match < 0) match = -1;
        Select(match);
        UpdateSubtitle();
    }

    private void Realign(Alignment mode)
    {
        if (Session is not { } session) return;
        session.Align(mode);
        Rows.OnAligned();
        DeltaCount = session.Deltas.Count;
        UpdateFiles();
        if (Rows.Selected >= 0) Detail = session.Matches[Rows.Selected];
        RebuildEntities();
        UpdateSubtitle();
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
        if (_a is not null) _a.DataChanged -= _onDocumentData;
        if (_b is not null) _b.DataChanged -= _onDocumentData;
        _a = a;
        _b = b;
        if (_a is not null) _a.DataChanged += _onDocumentData;
        if (_b is not null) _b.DataChanged += _onDocumentData;
        OnPropertyChanged(nameof(DocumentA));
        OnPropertyChanged(nameof(DocumentB));
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
        _documents.PropertyChanged -= OnDocumentsChanged;
        _documents.Documents.CollectionChanged -= OnTabsChanged;
        _chrome.PropertyChanged -= OnChromeChanged;
        Track(null, null);
    }
}
