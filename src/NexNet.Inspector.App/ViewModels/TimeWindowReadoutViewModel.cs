using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NexNet.Inspector.App.Controls.Compare;
using NexNet.Inspector.App.Model;
using NexNet.Inspector.App.Services;
using NexNet.Inspector.Core.Compare;
using NexNet.Inspector.Core.Model;

namespace NexNet.Inspector.App.ViewModels;

/// <summary>
/// The title bar's time window (controls/window-title-bar item 8): − · the visible range from the active capture's start
/// ("1,240.0 – 1,290.0 ms", in seconds once the range reaches 10 s) · +. The buttons zoom the shared window around its
/// centre by a factor of two, as Ctrl+Wheel does around the pointer. In Compare it reads and zooms the compare's own
/// window (compare time, 0 = the earlier aligned start) instead of the active document's.
/// </summary>
public sealed partial class TimeWindowReadoutViewModel : ObservableObject
{
    /// <summary>How much − and + zoom out and in.</summary>
    public const double ZoomStep = 2;

    private readonly DocumentsViewModel _documents;
    private CompareRowsModel? _compare;
    private DocumentViewModel? _active;
    private Func<bool>? _isCompare;

    /// <param name="documents">The documents and their shared sync bus.</param>
    public TimeWindowReadoutViewModel(DocumentsViewModel documents)
    {
        ArgumentNullException.ThrowIfNull(documents);
        _documents = documents;
        documents.Sync.Changed += OnSyncChanged;
        documents.PropertyChanged += OnDocumentsChanged;
        _active = documents.Active;
        if (_active is not null) _active.ScopeChanged += OnScopeChanged;
        Update();
    }

    /// <summary>The visible range, or empty when there is no window yet.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWindow))]
    [NotifyCanExecuteChangedFor(nameof(ZoomInCommand), nameof(ZoomOutCommand))]
    public partial string Text { get; private set; } = "";

    /// <summary>True when a window is shown (a document is open and has data).</summary>
    public bool HasWindow => Text.Length > 0;

    /// <summary>The scope chip's text, "Scope 1,240.0–1,290.0 ms"; empty while the active document is not scoped.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasScope))]
    [NotifyCanExecuteChangedFor(nameof(BackCommand), nameof(ClearScopeCommand))]
    public partial string ScopeText { get; private set; } = "";

    /// <summary>True while the active document is scoped: the chip shows.</summary>
    public bool HasScope => ScopeText.Length > 0;

    /// <summary>The chip's depth, "‹2", while scopes are nested; empty otherwise.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNested))]
    public partial string ScopeDepthText { get; private set; } = "";

    /// <summary>True while scopes are nested (the chip shows its depth).</summary>
    public bool IsNested => ScopeDepthText.Length > 0;

    /// <summary>The chip's tooltip: where the scope came from, and what Back and ✕ do.</summary>
    [ObservableProperty]
    public partial string ScopeTip { get; private set; } = "";

    /// <summary>"1,240.0–1,290.0 ms" (or "364.7–365.6 s" past 10 s): a scope from <paramref name="origin"/>, in the capture's ticks.</summary>
    public static string FormatScope(TimeWindow scope, long origin, long frequency)
    {
        double from = TimeWindow.MillisecondsFromTicks(scope.Start - origin, Math.Max(1, frequency));
        double to = TimeWindow.MillisecondsFromTicks(scope.End - origin, Math.Max(1, frequency));
        return Math.Max(Math.Abs(from), Math.Abs(to)) >= 10_000
            ? (from / 1000).ToString("N1", CultureInfo.InvariantCulture) + "–" + (to / 1000).ToString("N1", CultureInfo.InvariantCulture) + " s"
            : from.ToString("N1", CultureInfo.InvariantCulture) + "–" + to.ToString("N1", CultureInfo.InvariantCulture) + " ms";
    }

    /// <summary>The chip (and Alt+Left): back to the previous scope, and the view shown before this one.</summary>
    [RelayCommand(CanExecute = nameof(HasScope))]
    private void Back() => _documents.PopScope();

    /// <summary>The chip's ✕: clears every scope.</summary>
    [RelayCommand(CanExecute = nameof(HasScope))]
    private void ClearScope() => _documents.ClearScope();

    /// <summary>"1,240.0 – 1,290.0 ms" (or "0.0 – 42.1 s"): a window from <paramref name="origin"/>, in the capture's ticks.</summary>
    public static string Format(TimeWindow window, long origin, long frequency)
    {
        double from = TimeWindow.MillisecondsFromTicks(window.Start - origin, Math.Max(1, frequency));
        double to = TimeWindow.MillisecondsFromTicks(window.End - origin, Math.Max(1, frequency));
        return Math.Max(Math.Abs(from), Math.Abs(to)) >= 10_000
            ? (from / 1000).ToString("N1", CultureInfo.InvariantCulture) + " – " + (to / 1000).ToString("N1", CultureInfo.InvariantCulture) + " s"
            : from.ToString("N1", CultureInfo.InvariantCulture) + " – " + to.ToString("N1", CultureInfo.InvariantCulture) + " ms";
    }

    /// <summary>
    /// Follows the Compare workspace while &lt;paramref name="isCompare"/&gt; holds: the readout shows and zooms the compare's
    /// window. The caller raises &lt;see cref="OnWorkspaceChanged"/&gt; when the workspace switches.
    /// </summary>
    internal void FollowCompare(CompareRowsModel rows, Func<bool> isCompare)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(isCompare);
        _compare = rows;
        _isCompare = isCompare;
        rows.Changed += changes =>
        {
            if ((changes & (CompareChanges.Window | CompareChanges.Rows)) != 0) Update();
        };
        Update();
    }

    /// <summary>The Inspect | Compare switch moved: the readout follows the shown workspace's window.</summary>
    internal void OnWorkspaceChanged() => Update();

    // The compare's rows when Compare shows and has a session; null otherwise.
    private CompareRowsModel? ShownCompare => _isCompare?.Invoke() == true && _compare is { Session: not null } rows ? rows : null;

    /// <summary>+: half the range, around its centre.</summary>
    [RelayCommand(CanExecute = nameof(HasWindow))]
    private void ZoomIn() => Zoom(1 / ZoomStep);

    /// <summary>−: twice the range, around its centre.</summary>
    [RelayCommand(CanExecute = nameof(HasWindow))]
    private void ZoomOut() => Zoom(ZoomStep);

    private void Zoom(double factor)
    {
        if (ShownCompare is { } compare)
        {
            if (!compare.Window.IsEmpty) compare.ZoomAround(compare.Window.Center, factor);
            return;
        }

        SyncBus sync = _documents.Sync;
        if (sync.Window.IsEmpty) return;
        sync.ZoomAround(sync.Window.Center, factor);
    }

    private void OnSyncChanged(SyncChanges changes)
    {
        if ((changes & (SyncChanges.Window | SyncChanges.Bounds | SyncChanges.Scope)) != 0) Update();
    }

    private void OnDocumentsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(DocumentsViewModel.Active) or nameof(DocumentsViewModel.HasDocuments))) return;
        if (!ReferenceEquals(_active, _documents.Active))
        {
            // A nested scope equal to its parent changes the depth only: the chip listens to the document itself.
            if (_active is not null) _active.ScopeChanged -= OnScopeChanged;
            _active = _documents.Active;
            if (_active is not null) _active.ScopeChanged += OnScopeChanged;
        }

        Update();
    }

    private void OnScopeChanged(DocumentViewModel document, ScopeChange change) => UpdateScope();

    private void Update()
    {
        UpdateScope();
        if (_isCompare?.Invoke() == true)
        {
            Text = ShownCompare is { Window.IsEmpty: false } compare ? Format(compare.Window, 0, CompareClock.Frequency) : "";
            return;
        }

        TimeWindow window = _documents.Sync.Window;
        Text = _documents.Active is { Session: { } session } document && !window.IsEmpty
            ? Format(window, session.StartTicks, document.Frequency)
            : "";
    }

    // The chip follows the active document's scope (in Compare too: the scope applies to the aligned axis).
    private void UpdateScope()
    {
        if (_documents.Active is not { Session: { } session, Scope: { } scope } document)
        {
            ScopeText = "";
            ScopeDepthText = "";
            ScopeTip = "";
            return;
        }

        int depth = document.ScopeDepth;
        ScopeText = "Scope " + FormatScope(scope.Window, session.StartTicks, document.Frequency);
        ScopeDepthText = depth > 1 ? "‹" + depth.ToString(CultureInfo.InvariantCulture) : "";
        ScopeTip = (scope.Label.Length > 0 ? scope.Label + " · " : "")
                   + (depth > 1 ? "Back to the previous scope (Alt+Left)" : "Back to the whole session (Alt+Left)") + " · ✕ clears every scope";
    }
}
