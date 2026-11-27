using Avalonia.Threading;

namespace NexNet.Inspector.App.Services;

/// <summary>
/// Runs work on the UI thread. Models and view-models post through this instead of Avalonia's dispatcher, so they run
/// in tests without a UI; <see cref="AvaloniaUiDispatcher"/> is the one place that talks to Avalonia.
/// </summary>
public interface IUiDispatcher
{
    /// <summary>Queues <paramref name="action"/> to run on the UI thread.</summary>
    void Post(Action action);
}

/// <summary>Posts to Avalonia's UI thread at render priority, so a coalesced change lands before the next frame is drawn.</summary>
public sealed class AvaloniaUiDispatcher : IUiDispatcher
{
    /// <summary>The shared instance.</summary>
    public static AvaloniaUiDispatcher Instance { get; } = new();

    /// <inheritdoc/>
    public void Post(Action action) => Dispatcher.UIThread.Post(action, DispatcherPriority.Render);
}

/// <summary>
/// Collapses any number of change signals, from any thread, into one callback on the UI thread: the first signal posts
/// it, later ones until it runs are free. A live capture can add thousands of frames between two drawn frames; the
/// views hear about them once.
/// </summary>
public sealed class ChangeCoalescer
{
    private readonly IUiDispatcher _ui;
    private readonly Action _flush;
    private readonly Action _run;
    private int _pending;

    /// <param name="ui">Where <paramref name="flush"/> runs.</param>
    /// <param name="flush">The callback; it reads the current state, whatever the number of signals.</param>
    public ChangeCoalescer(IUiDispatcher ui, Action flush)
    {
        ArgumentNullException.ThrowIfNull(ui);
        ArgumentNullException.ThrowIfNull(flush);
        _ui = ui;
        _flush = flush;
        _run = Run;
    }

    /// <summary>Notes a change; safe from any thread, allocation-free.</summary>
    public void Signal()
    {
        if (Interlocked.Exchange(ref _pending, 1) == 0)
            _ui.Post(_run);
    }

    private void Run()
    {
        // Cleared before the flush, so a change made while it runs posts again rather than being lost.
        Volatile.Write(ref _pending, 0);
        _flush();
    }
}
