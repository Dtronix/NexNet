using CommunityToolkit.Mvvm.ComponentModel;
using NexNet.Inspector.App.Controls.Primitives;
using NexNet.Inspector.App.Model;
using NexNet.Inspector.App.Model.Palette;
using NexNet.Inspector.App.Services;
using NexNet.Inspector.App.Services.Commands;
using NexNet.Inspector.Core.Assembly;
using NexNet.Inspector.Core.Model;
using NexNet.Inspector.Core.Session;

namespace NexNet.Inspector.App.ViewModels;

/// <summary>
/// The command palette (Ctrl+Shift+P; SYSTEM.md "command-palette"): a query, the scope pills, and the grouped results
/// of <see cref="PaletteSearch"/> with one keyboard item. Typing a prefix switches the scope (<c>&gt;</c> commands,
/// <c>#</c> frames, <c>@</c> sessions, <c>:</c> time) and the pill follows; picking a pill drops the prefix. Enter runs
/// the item: an entity is selected (and so traced) on the sync bus, a frame selected, a time jumps the playhead and
/// window, a session activated, a command run. Either way the palette closes first.
/// </summary>
public sealed partial class CommandPaletteViewModel : ObservableObject
{
    /// <summary>The footer's key hints.</summary>
    public const string Hints = "↑↓ move    Enter run    Tab switch scope    > commands · # frames · @ sessions · : time";

    private readonly DocumentsViewModel _documents;
    private readonly CommandRegistry _commands;
    private readonly PaletteSearch _search = new();
    private bool _syncingScope;

    /// <param name="documents">The open documents and the sync bus navigation moves.</param>
    /// <param name="commands">The commands listed and run.</param>
    public CommandPaletteViewModel(DocumentsViewModel documents, CommandRegistry commands)
    {
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentNullException.ThrowIfNull(commands);
        _documents = documents;
        _commands = commands;
        Scope = new EnumChoice<PaletteScope>(
        [
            (PaletteScope.All, "All"), (PaletteScope.Commands, "Commands"), (PaletteScope.Frames, "Frames"),
            (PaletteScope.Entities, "Entities"), (PaletteScope.Sessions, "Sessions"),
        ], PaletteScope.All);
        Scope.ValueChanged += OnScopePicked;
    }

    /// <summary>True while the palette shows.</summary>
    [ObservableProperty]
    public partial bool IsOpen { get; private set; }

    /// <summary>The query as typed, prefix included.</summary>
    [ObservableProperty]
    public partial string Query { get; set; } = "";

    /// <summary>The scope pills: All | Commands | Frames | Entities | Sessions.</summary>
    public EnumChoice<PaletteScope> Scope { get; }

    /// <summary>The results: group headings and items, in display order.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<PaletteRow> Rows { get; private set; } = [];

    /// <summary>The keyboard's item in <see cref="Rows"/>; -1 when there is none (never a heading).</summary>
    [ObservableProperty]
    public partial int SelectedIndex { get; private set; } = -1;

    /// <summary>The keyboard's item.</summary>
    public PaletteRow? Selected => SelectedIndex >= 0 ? Rows[SelectedIndex] : null;

    /// <summary>The effective scope: the prefix's, else the pill's.</summary>
    public PaletteScope ActiveScope => PaletteQuery.Parse(Query, Scope.Value).Scope;

    /// <summary>True when the query found nothing.</summary>
    public bool IsEmpty => Rows.Count == 0;

    /// <summary>Raised after the palette closed (the view hands the keyboard back).</summary>
    public event Action? Closed;

    /// <summary>Opens the palette (or, when open, replaces its query).</summary>
    /// <param name="query">The initial query (":" for Go to time); empty by default.</param>
    public void Open(string? query = null)
    {
        _syncingScope = true;
        Scope.Value = PaletteScope.All;
        _syncingScope = false;
        if (Query == (query ?? "")) Refresh();
        else Query = query ?? "";
        IsOpen = true;
    }

    /// <summary>Closes the palette.</summary>
    public void Close()
    {
        if (!IsOpen) return;
        IsOpen = false;
        Closed?.Invoke();
    }

    /// <summary>Runs the search again (the session grew, a command's state changed).</summary>
    public void Refresh()
    {
        PaletteQuery query = PaletteQuery.Parse(Query, Scope.Value);
        CaptureSession? session = _documents.Active?.Session;
        List<PaletteRow> rows = _search.Run(query, session, Documents(), _commands);
        if (SelectedIndex >= 0 && SelectedIndex < Rows.Count) Rows[SelectedIndex].IsSelected = false;
        SelectedIndex = -1;
        Rows = rows;
        OnPropertyChanged(nameof(IsEmpty));
        Select(FirstItem(0, 1, enabledOnly: true) is var first and >= 0 ? first : FirstItem(0, 1, enabledOnly: false));
    }

    /// <summary>Moves the keyboard item by <paramref name="delta"/> items (headings skipped), stopping at either end.</summary>
    public void Move(int delta)
    {
        if (delta == 0 || Rows.Count == 0) return;
        int step = Math.Sign(delta);
        int at = SelectedIndex;
        for (int moved = 0; moved < Math.Abs(delta); moved++)
        {
            int next = FirstItem(at < 0 ? (step > 0 ? 0 : Rows.Count - 1) : at + step, step, enabledOnly: false);
            if (next < 0) break;
            at = next;
        }

        Select(at);
    }

    /// <summary>Moves the keyboard item to the first or the last item.</summary>
    public void MoveToEdge(bool last) => Select(last ? FirstItem(Rows.Count - 1, -1, enabledOnly: false) : FirstItem(0, 1, enabledOnly: false));

    /// <summary>Makes <paramref name="row"/> the keyboard item (a pointer press on it).</summary>
    public void Select(PaletteRow row)
    {
        int index = IndexOf(row);
        if (index >= 0 && Rows[index].IsItem) Select(index);
    }

    /// <summary>Tab: the next pill (Shift+Tab the previous); a prefix in the query is dropped.</summary>
    public void CycleScope(bool backwards)
    {
        int count = Scope.Values.Count;
        int current = Scope.IndexOf(PaletteQuery.Parse(Query, Scope.Value).Scope is var scope && scope == PaletteScope.Time ? PaletteScope.All : scope);
        Scope.SelectedIndex = (current + (backwards ? count - 1 : 1)) % count;
    }

    /// <summary>Enter: runs the keyboard item.</summary>
    /// <returns>True when something ran.</returns>
    public bool Run() => Selected is { } row && Run(row);

    /// <summary>Runs <paramref name="row"/>: navigates or runs its command, closing the palette first.</summary>
    /// <returns>True when something ran.</returns>
    public bool Run(PaletteRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (!row.IsEnabled) return false;
        PaletteTarget target = row.Target;
        Close();
        switch (target.Kind)
        {
            case PaletteTargetKind.Command:
                return _commands.Execute(target.Command);
            case PaletteTargetKind.Session:
                if ((uint)target.Index >= (uint)_documents.Documents.Count) return false;
                _documents.Activate(_documents.Documents[target.Index]);
                return true;
            case PaletteTargetKind.Entity:
                return Navigate(Selection.OfEntity(target.Entity, target.Index), target.Start, target.End);
            case PaletteTargetKind.Frame:
                return Navigate(Selection.OfFrame(target.Frame), target.Start, target.End);
            case PaletteTargetKind.Time:
                return JumpTo(target.Start);
            default:
                return false;
        }
    }

    /// <summary>
    /// The window after revealing <paramref name="start"/>–<paramref name="end"/>: unchanged when the span is in view;
    /// otherwise the same zoom, centred on the span, or starting just before it when it is longer than the window.
    /// </summary>
    public static TimeWindow Reveal(TimeWindow window, long start, long end)
    {
        if (end < start) (start, end) = (end, start);
        if (window.IsEmpty) return TimeWindow.FromDuration(start, Math.Max(end - start, TimeWindow.MinDuration));
        if (start >= window.Start && end <= window.End) return window;
        long duration = window.Duration;
        if (end - start <= duration * 8 / 10)
        {
            long centre = start + ((end - start) / 2);
            return TimeWindow.FromDuration(centre - (duration / 2), duration);
        }

        return TimeWindow.FromDuration(start - (duration / 10), duration);
    }

    partial void OnQueryChanged(string value)
    {
        // A typed prefix moves the pill to its scope (":" time has none: All).
        PaletteQuery query = PaletteQuery.Parse(value, Scope.Value);
        if (query.HasPrefix)
        {
            _syncingScope = true;
            Scope.Value = query.Scope == PaletteScope.Time ? PaletteScope.All : query.Scope;
            _syncingScope = false;
        }

        OnPropertyChanged(nameof(ActiveScope));
        Refresh();
    }

    partial void OnSelectedIndexChanged(int value) => OnPropertyChanged(nameof(Selected));

    private void OnScopePicked(object? sender, PaletteScope scope)
    {
        if (_syncingScope) return;
        // A picked pill replaces whatever prefix was typed: the query keeps only its text.
        PaletteQuery query = PaletteQuery.Parse(Query, scope);
        if (query.HasPrefix) Query = query.Text;
        else Refresh();
        OnPropertyChanged(nameof(ActiveScope));
    }

    private bool Navigate(in Selection selection, long start, long end)
    {
        if (_documents.Active?.Session is null) return false;
        SyncBus sync = _documents.Sync;
        sync.SetWindow(Reveal(sync.Window, start, end));
        sync.Select(selection, start);
        return true;
    }

    private bool JumpTo(long ticks)
    {
        if (_documents.Active?.Session is null) return false;
        SyncBus sync = _documents.Sync;
        sync.SetWindow(Reveal(sync.Window, ticks, ticks));
        sync.SetPlayhead(ticks);
        return true;
    }

    private List<PaletteDocument> Documents()
    {
        List<PaletteDocument> list = new(_documents.Documents.Count);
        foreach (DocumentViewModel document in _documents.Documents)
        {
            string frames = document.FrameCount.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) + " frames";
            string detail = document is LiveDocumentViewModel live ? "live · " + StateText(live.State) + " · " + frames : "recording · " + frames;
            list.Add(new PaletteDocument(document.Title, document.IsLive, detail, ReferenceEquals(document, _documents.Active)));
        }

        return list;
    }

    private static string StateText(LiveState state) => state switch
    {
        LiveState.Attaching => "attaching",
        LiveState.Live => "attached",
        LiveState.Ended => "ended",
        LiveState.Detached => "detached",
        _ => "failed",
    };

    private int FirstItem(int from, int step, bool enabledOnly)
    {
        for (int i = from; i >= 0 && i < Rows.Count; i += step)
        {
            if (Rows[i].IsItem && (!enabledOnly || Rows[i].IsEnabled)) return i;
        }

        return -1;
    }

    private int IndexOf(PaletteRow row)
    {
        for (int i = 0; i < Rows.Count; i++)
        {
            if (ReferenceEquals(Rows[i], row)) return i;
        }

        return -1;
    }

    private void Select(int index)
    {
        if (index == SelectedIndex) return;
        if (SelectedIndex >= 0 && SelectedIndex < Rows.Count) Rows[SelectedIndex].IsSelected = false;
        SelectedIndex = index;
        if (index >= 0) Rows[index].IsSelected = true;
    }
}
