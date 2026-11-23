using NexNet.Inspector.App.Model;
using NexNet.Inspector.Core.Model;

namespace NexNet.Inspector.App.Services;

/// <summary>What changed on the <see cref="SyncBus"/> since its last notification.</summary>
[Flags]
public enum SyncChanges : byte
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
    private int _changes;

    /// <param name="ui">Where <see cref="Changed"/> is raised.</param>
    public SyncBus(IUiDispatcher ui) => _notify = new ChangeCoalescer(ui, Flush);

    /// <summary>Raised on the UI thread, at most once per UI turn, with what changed since the last time.</summary>
    public event Action<SyncChanges>? Changed;

    /// <summary>The time window, in Stopwatch ticks of the active document.</summary>
    public TimeWindow Window { get; private set; }

    /// <summary>The time range the window is kept in: the active document's capture (empty: unbounded).</summary>
    public TimeWindow Bounds { get; private set; }

    /// <summary>The playhead tick.</summary>
    public long Playhead { get; private set; }

    /// <summary>The selection of the active document.</summary>
    public Selection Selection { get; private set; } = Selection.None;

    /// <summary>The decoded field under the pointer.</summary>
    public FieldHover Hover { get; private set; } = FieldHover.None;

    /// <summary>True while the window keeps its right edge on the newest data of a live document.</summary>
    public bool FollowLive { get; private set; }

    /// <summary>Sets the window, kept inside <see cref="Bounds"/>. Moving it by hand stops following live.</summary>
    /// <param name="window">The new window.</param>
    /// <param name="byUser">True for a user's zoom or pan, which stops following the live edge.</param>
    public void SetWindow(TimeWindow window, bool byUser = true)
    {
        if (!Bounds.IsEmpty)
            window = window.Clamp(Bounds.Start, Math.Max(Bounds.End, Bounds.Start + window.Duration));
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

    /// <summary>Sets the bounds the window is kept in.</summary>
    public void SetBounds(TimeWindow bounds)
    {
        if (bounds == Bounds)
            return;
        Bounds = bounds;
        Mark(SyncChanges.Bounds);
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
            Mark(SyncChanges.Selection);
        }

        if (ticks is { } t)
            SetPlayhead(t);
    }

    /// <summary>Sets the hovered decoded field.</summary>
    public void SetHover(FieldHover hover)
    {
        if (hover == Hover)
            return;
        Hover = hover;
        Mark(SyncChanges.Hover);
    }

    /// <summary>Turns following the live edge on or off.</summary>
    public void SetFollowLive(bool follow)
    {
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
        if (!FollowLive || latest <= Window.End)
            return;
        Window = Window.Follow(latest);
        Mark(SyncChanges.Window);
    }

    /// <summary>Replaces every value at once, as a document switch does.</summary>
    internal void Restore(TimeWindow window, TimeWindow bounds, long playhead, Selection selection, bool followLive)
    {
        Bounds = bounds;
        Window = window;
        Playhead = playhead;
        Selection = selection;
        Hover = FieldHover.None;
        FollowLive = followLive;
        Mark(SyncChanges.Window | SyncChanges.Bounds | SyncChanges.Playhead | SyncChanges.Selection | SyncChanges.Hover
             | SyncChanges.FollowLive);
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
