using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using NexNet.Inspector.App.Model;
using NexNet.Inspector.App.Services;
using NexNet.Inspector.Core.Model;
using NexNet.Inspector.Core.Session;

namespace NexNet.Inspector.App.ViewModels;

/// <summary>
/// One document tab: a live attach or one session of a recording, over a <see cref="CaptureSession"/>. Its observable
/// counts and state are refreshed on the UI thread, at most once per UI turn however fast data arrives (the session is
/// fed on a background thread); views read rows from <see cref="Session"/> under its gate.
/// </summary>
public abstract partial class DocumentViewModel : ObservableObject, IAsyncDisposable
{
    private static int s_nextId;

    private readonly ChangeCoalescer _changes;
    private long _seenVersion = -1;

    /// <param name="kind">Live or file.</param>
    /// <param name="ui">Where refreshes run.</param>
    protected DocumentViewModel(DocumentKind kind, IUiDispatcher ui)
    {
        ArgumentNullException.ThrowIfNull(ui);
        Id = Interlocked.Increment(ref s_nextId);
        Kind = kind;
        Ui = ui;
        _changes = new ChangeCoalescer(ui, RefreshNow);
        Scopes.Changed += change =>
        {
            OnPropertyChanged(nameof(Scope));
            OnPropertyChanged(nameof(ScopeDepth));
            OnPropertyChanged(nameof(IsScoped));
            ScopeChanged?.Invoke(this, change);
        };
    }

    /// <summary>Raised after the document's scope was pushed, popped or cleared (the documents move the views with it).</summary>
    public event Action<DocumentViewModel, ScopeChange>? ScopeChanged;

    /// <summary>
    /// The document's nested scopes (D2, "Filter to time range"): every view of the document is limited to the current
    /// one. Not persisted; a bookmark (I44) can push one again.
    /// </summary>
    public ScopeStack Scopes { get; } = new();

    /// <summary>The active scope; null when the document is not scoped.</summary>
    public TimeScope? Scope => Scopes.Current;

    /// <summary>How many scopes are nested (the chip's "‹2").</summary>
    public int ScopeDepth => Scopes.Depth;

    /// <summary>True while a scope is set.</summary>
    public bool IsScoped => !Scopes.IsEmpty;

    /// <summary>Limits the document to <paramref name="scope"/> (cut to the current scope, nested inside it).</summary>
    /// <param name="scope">The scope.</param>
    /// <param name="before">The window shown now, which Back returns to; empty when not known.</param>
    /// <returns>False when the scope covers no time inside the current one.</returns>
    public bool PushScope(TimeScope scope, TimeWindow before = default) => Scopes.Push(scope, before);

    /// <summary>Back: returns to the previous scope (or none).</summary>
    /// <returns>False when the document was not scoped.</returns>
    public bool PopScope() => Scopes.Pop();

    /// <summary>✕: clears every scope.</summary>
    /// <returns>False when the document was not scoped.</returns>
    public bool ClearScopes() => Scopes.Clear();

    /// <summary>Raised on the UI thread after a refresh that saw new data or a new state.</summary>
    public event Action<DocumentViewModel>? DataChanged;

    /// <summary>A process-unique id (tabs, palette entries).</summary>
    public int Id { get; }

    /// <summary>Live or file.</summary>
    public DocumentKind Kind { get; }

    /// <summary>Where refreshes run.</summary>
    protected IUiDispatcher Ui { get; }

    /// <summary>The tab's title.</summary>
    [ObservableProperty]
    public partial string Title { get; protected set; } = "";

    /// <summary>The captured session; null before it opened (live) or after a detach reset it.</summary>
    [ObservableProperty]
    public partial CaptureSession? Session { get; protected set; }

    /// <summary>Frames of both directions.</summary>
    [ObservableProperty]
    public partial int FrameCount { get; private set; }

    /// <summary>Transport ops of both directions.</summary>
    [ObservableProperty]
    public partial long OpCount { get; private set; }

    /// <summary>Bytes of both directions.</summary>
    [ObservableProperty]
    public partial long ByteCount { get; private set; }

    /// <summary>Gap blocks (dropped events).</summary>
    [ObservableProperty]
    public partial int GapCount { get; private set; }

    /// <summary>The capture's time range, in its Stopwatch ticks.</summary>
    [ObservableProperty]
    public partial TimeWindow TimeRange { get; private set; }

    /// <summary>Where the session stands.</summary>
    [ObservableProperty]
    public partial CaptureState CaptureState { get; private set; }

    /// <summary>How the session's embedded schema compares with the workspace's.</summary>
    [ObservableProperty]
    public partial SchemaStatus SchemaStatus { get; private set; }

    /// <summary>Which schema decodes.</summary>
    [ObservableProperty]
    public partial SchemaSource SchemaSource { get; private set; }

    /// <summary>True when closing would lose captured data (OQ5: the close prompt asks first).</summary>
    [ObservableProperty]
    public partial bool IsDirty { get; protected set; }

    /// <summary>Stopwatch ticks per second of the capture's clock.</summary>
    public long Frequency => Session?.Header.StopwatchFrequency ?? Stopwatch.Frequency;

    // The view state a document keeps while another tab is active.
    internal Selection SavedSelection { get; set; } = Selection.None;

    internal long SavedPlayhead { get; set; }

    // A live document follows its live edge until the user moves away from it.
    internal bool SavedFollowLive { get; set; } = true;

    // The window last fitted to a growing file, so a refit only replaces a window the user has not moved.
    internal TimeWindow FittedWindow { get; set; }

    /// <summary>Asks for a refresh on the UI thread; safe from any thread.</summary>
    protected void SignalChanged() => _changes.Signal();

    /// <summary>Releases the document's resources.</summary>
    public abstract ValueTask DisposeAsync();

    /// <summary>Refreshes the observable state from the session and the subclass's own state. UI thread.</summary>
    protected virtual void OnRefresh()
    {
    }

    /// <summary>Clears every count, as a detach does.</summary>
    protected void ResetCounts()
    {
        _seenVersion = -1;
        FrameCount = 0;
        OpCount = 0;
        ByteCount = 0;
        GapCount = 0;
        TimeRange = default;
        CaptureState = CaptureState.Open;
    }

    private void RefreshNow()
    {
        if (Session is { } session)
        {
            var version = session.Version;
            if (version != _seenVersion)
            {
                _seenVersion = version;
                lock (session.Gate)
                {
                    FrameCount = session.FrameCount;
                    OpCount = session.OpCount;
                    ByteCount = session.ByteCount;
                    GapCount = session.Gaps.Length;
                    TimeRange = new TimeWindow(session.StartTicks, Math.Max(session.EndTicks, session.StartTicks + 1));
                }

                CaptureState = session.State;
                SchemaStatus = session.Schema.Status;
                SchemaSource = session.Schema.Source;
            }
        }

        OnRefresh();
        DataChanged?.Invoke(this);
    }
}
