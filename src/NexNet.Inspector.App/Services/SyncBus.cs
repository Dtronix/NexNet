using NexNet.Inspector.App.Model;
using NexNet.Inspector.Core.Model;

namespace NexNet.Inspector.App.Services;

/// <summary>What changed on the <see cref="SyncBus"/> since its last notification.</summary>
[Flags]
public enum SyncChanges : ushort
{
    /// <summary>Nothing.</summary>
    None = 0,

    /// <summary>The time window (zoom or position).</summary>
    Window = 1 << 0,

    /// <summary>The playhead.</summary>
    Playhead = 1 << 1,

    /// <summary>The selection.</summary>
    Selection = 1 << 2,

    /// <summary>The hovered decoded field.</summary>
    Hover = 1 << 3,

    /// <summary>Whether the window follows the live edge.</summary>
    FollowLive = 1 << 4,

    /// <summary>The bounds the window is kept in (the active document's time range).</summary>
    Bounds = 1 << 5,

    /// <summary>The traced message (it follows the selection, and grows as a live message's frames arrive).</summary>
    Trace = 1 << 6,

    /// <summary>How the inspector reads the hex selection ("Decode selection as ▸").</summary>
    Decode = 1 << 7,

    /// <summary>The active document's scope (D2): the views limit themselves to it; <see cref="Bounds"/> changes with it.</summary>
    Scope = 1 << 8,
}

/// <summary>
/// The one time window, playhead, selection and hovered field that every view (timeline, events, ladder, hex,
/// inspector) follows. The timeline owns them; selecting anywhere moves the playhead; all documents share the window.
/// </summary>
/// <remarks>
/// Use it from the UI thread. Setters apply at once (readers always see the latest values), and
/// <see cref="Changed"/> fires once per UI turn with every kind of change since the last one, so a drag that moves the
/// window a hundred times between two frames redraws the views once.
/// </remarks>
public sealed class SyncBus
{
    private readonly ChangeCoalescer _notify;
    private Func<Selection, MessageTrace>? _resolver;
    private int _changes;
    private TimeWindow _range;
    private bool _resumeFollow;

    /// <param name="ui">Where <see cref="Changed"/> is raised.</param>
    public SyncBus(IUiDispatcher ui) => _notify = new ChangeCoalescer(ui, Flush);

    /// <summary>Raised on the UI thread, at most once per UI turn, with what changed since the last time.</summary>
    public event Action<SyncChanges>? Changed;

    /// <summary>The time window, in Stopwatch ticks of the active document.</summary>
    public TimeWindow Window { get; private set; }

    /// <summary>
    /// The time range the window is kept in: the active document's scope while it has one (<see cref="Scope"/>), else
    /// its capture (empty: unbounded, a live document).
    /// </summary>
    public TimeWindow Bounds => Scope.IsEmpty ? _range : Scope;

    /// <summary>The active document's capture range, whatever its scope (empty for a live document).</summary>
    public TimeWindow CaptureBounds => _range;

    /// <summary>
    /// The active document's scope (D2, "Filter to time range"); empty when it has none. The window stays inside it, the
    /// timeline's overview and ruler span it, the panes list only what falls in it, and following the live edge is
    /// suspended while it is set (a live scope stays fixed while data arrives after it).
    /// </summary>
    public TimeWindow Scope { get; private set; }

    /// <summary>True while the active document is scoped.</summary>
    public bool IsScoped => !Scope.IsEmpty;

    /// <summary>True when following the live edge comes back once the scope is cleared (it was on when the scope was set).</summary>
    public bool ResumesFollowLive => IsScoped ? _resumeFollow : FollowLive;

    /// <summary>The playhead tick.</summary>
    public long Playhead { get; private set; }

    /// <summary>The selection of the active document.</summary>
    public Selection Selection { get; private set; } = Selection.None;

    /// <summary>The decoded field under the pointer.</summary>
    public FieldHover Hover { get; private set; } = FieldHover.None;

    /// <summary>How the inspector reads the selected bytes; <see cref="SelectionDecode.Auto"/> unless the user picked one.</summary>
    public SelectionDecode Decode { get; private set; } = SelectionDecode.Auto;

    /// <summary>True while the window keeps its right edge on the newest data of a live document.</summary>
    public bool FollowLive { get; private set; }

    /// <summary>
    /// The traced message: what the selection traces in the active document (<see cref="TraceResolver"/>), resolved once
    /// here so the timeline band, the ladder bracket, the Events tint and the inspector's Message trace tab agree.
    /// </summary>
    public MessageTrace Trace { get; private set; } = MessageTrace.None;

    /// <summary>
    /// Resolves a selection to its trace (the documents set it to read the active document's session); null resolves
    /// nothing, and the views fall back to tracing from the selection by themselves.
    /// </summary>
    public Func<Selection, MessageTrace>? TraceResolver
    {
        get => _resolver;
        set
        {
            _resolver = value;
            RefreshTrace();
        }
    }

    /// <summary>True when <see cref="Trace"/> is resolved by <see cref="TraceResolver"/>; views then draw it.</summary>
    public bool ResolvesTrace => _resolver is not null;

    /// <summary>Sets the window, kept inside <see cref="Bounds"/>. Moving it by hand stops following live.</summary>
    /// <param name="window">The new window.</param>
    /// <param name="byUser">True for a user's zoom or pan, which stops following the live edge.</param>
    public void SetWindow(TimeWindow window, bool byUser = true)
    {
        // A scope holds the window inside it (never wider: the overview spans exactly the scope); a capture's range lets
        // a zoom out reach past its end.
        if (IsScoped)
            window = window.Clamp(Scope.Start, Scope.End);
        else if (!_range.IsEmpty)
            window = window.Clamp(_range.Start, Math.Max(_range.End, _range.Start + window.Duration));
        if (byUser && FollowLive && window.End < Window.End)
            SetFollowLive(false);
        if (window == Window)
            return;
        Window = window;
        Mark(SyncChanges.Window);
    }

    /// <summary>Zooms around a tick (Ctrl+Wheel zooms around the pointer).</summary>
    public void ZoomAround(long anchor, double factor) => SetWindow(Window.ZoomAround(anchor, factor));

    /// <summary>Pans the window by ticks.</summary>
    public void Pan(long delta) => SetWindow(Window.Pan(delta));

    /// <summary>Sets the capture range the window is kept in (a scope, while set, bounds it instead).</summary>
    public void SetBounds(TimeWindow bounds)
    {
        if (bounds == _range)
            return;
        _range = bounds;
        if (!IsScoped)
            Mark(SyncChanges.Bounds);
    }

    /// <summary>
    /// Sets the active document's scope (empty clears it): the window is moved inside it, and following the live edge is
    /// suspended until the scope is cleared, when it resumes if it was on.
    /// </summary>
    public void SetScope(TimeWindow scope)
    {
        if (scope.IsEmpty)
            scope = default;
        if (scope == Scope)
            return;

        bool wasScoped = IsScoped;
        SyncChanges changes = SyncChanges.Scope | SyncChanges.Bounds;
        Scope = scope;
        if (!scope.IsEmpty)
        {
            if (!wasScoped)
                _resumeFollow = FollowLive;
            if (FollowLive)
            {
                FollowLive = false;
                changes |= SyncChanges.FollowLive;
            }

            TimeWindow window = Window.IsEmpty ? scope : Window.Clamp(scope.Start, scope.End);
            if (window != Window)
            {
                Window = window;
                changes |= SyncChanges.Window;
            }
        }
        else if (_resumeFollow)
        {
            _resumeFollow = false;
            FollowLive = true;
            changes |= SyncChanges.FollowLive;
        }

        Mark(changes);
    }

    /// <summary>Moves the playhead.</summary>
    public void SetPlayhead(long ticks)
    {
        if (ticks == Playhead)
            return;
        Playhead = ticks;
        Mark(SyncChanges.Playhead);
    }

    /// <summary>Selects something; with a tick, the playhead moves there too (selecting anywhere moves the playhead).</summary>
    /// <param name="selection">The selection.</param>
    /// <param name="ticks">Where it happened, when known.</param>
    public void Select(Selection selection, long? ticks = null)
    {
        if (selection != Selection)
        {
            Selection = selection;
            // A decode the user picked reads its own bytes: a selection inside them (a decoded row's click) keeps it,
            // anything else ends it.
            if (!Decode.IsAuto && !Decode.Covers(selection))
            {
                Decode = SelectionDecode.Auto;
                Mark(SyncChanges.Decode);
            }

            // In the same notification as the selection, so no view draws the new selection with the old trace.
            Mark(ResolveTrace() ? SyncChanges.Selection | SyncChanges.Trace : SyncChanges.Selection);
        }

        if (ticks is { } t)
            SetPlayhead(t);
    }

    /// <summary>
    /// Reads the selected bytes another way (the hex menu's "Decode selection as ▸"); <see cref="DecodeAs.Auto"/> goes back
    /// to the schema's decode. Only a byte-range selection can be decoded so.
    /// </summary>
    public void SetDecode(DecodeAs mode)
    {
        SelectionDecode decode = mode == DecodeAs.Auto || Selection.Kind != SelectionKind.ByteRange
            ? SelectionDecode.Auto
            : new SelectionDecode(mode, Selection.Direction, Selection.Bytes);
        if (decode == Decode)
            return;
        Decode = decode;
        Mark(SyncChanges.Decode);
    }

    /// <summary>Sets the hovered decoded field.</summary>
    public void SetHover(FieldHover hover)
    {
        if (hover == Hover)
            return;
        Hover = hover;
        Mark(SyncChanges.Hover);
    }

    /// <summary>
    /// Resolves the trace of the current selection again (the active document grew or changed); a trace that came out
    /// the same is kept, so the views are told only when it changed.
    /// </summary>
    public void RefreshTrace()
    {
        if (ResolveTrace())
            Mark(SyncChanges.Trace);
    }

    // Resolves the selection's trace; true when it changed.
    private bool ResolveTrace()
    {
        MessageTrace trace = _resolver is { } resolve && !Selection.IsNone ? resolve(Selection) : MessageTrace.None;
        if (trace.SameAs(Trace))
            return false;
        Trace = trace;
        return true;
    }

    /// <summary>Turns following the live edge on or off.</summary>
    public void SetFollowLive(bool follow)
    {
        if (IsScoped)
        {
            // Suspended while scoped: the choice applies once the scope is cleared.
            _resumeFollow = follow;
            return;
        }

        if (follow == FollowLive)
            return;
        FollowLive = follow;
        Mark(SyncChanges.FollowLive);
    }

    /// <summary>
    /// The live document grew to <paramref name="latest"/>: when following, the window slides to keep its right edge
    /// there, at the same zoom.
    /// </summary>
    public void OnLiveData(long latest)
    {
        if (!FollowLive || IsScoped || latest <= Window.End)
            return;
        Window = Window.Follow(latest);
        Mark(SyncChanges.Window);
    }

    /// <summary>Replaces every value at once, as a document switch does.</summary>
    /// <param name="window">The window.</param>
    /// <param name="bounds">The document's capture range (empty for a live document).</param>
    /// <param name="playhead">The playhead.</param>
    /// <param name="selection">The selection.</param>
    /// <param name="followLive">Whether the window follows the live edge (once any scope is cleared).</param>
    /// <param name="scope">The document's scope; empty for none (following the live edge is then suspended).</param>
    internal void Restore(TimeWindow window, TimeWindow bounds, long playhead, Selection selection, bool followLive, TimeWindow scope = default)
    {
        _range = bounds;
        Scope = scope.IsEmpty ? default : scope;
        _resumeFollow = IsScoped && followLive;
        Window = IsScoped && !window.IsEmpty ? window.Clamp(Scope.Start, Scope.End) : IsScoped ? Scope : window;
        Playhead = playhead;
        Selection = selection;
        Hover = FieldHover.None;
        Decode = SelectionDecode.Auto;
        FollowLive = followLive && !IsScoped;
        Trace = _resolver is { } resolve && !selection.IsNone ? resolve(selection) : MessageTrace.None;
        Mark(SyncChanges.Window | SyncChanges.Bounds | SyncChanges.Playhead | SyncChanges.Selection | SyncChanges.Hover
             | SyncChanges.FollowLive | SyncChanges.Trace | SyncChanges.Decode | SyncChanges.Scope);
    }

    private void Mark(SyncChanges change)
    {
        Interlocked.Or(ref _changes, (int)change);
        _notify.Signal();
    }

    private void Flush()
    {
        var changes = (SyncChanges)Interlocked.Exchange(ref _changes, 0);
        if (changes != SyncChanges.None)
            Changed?.Invoke(changes);
    }
}
