using NexNet.Inspector.App.Services;

namespace NexNet.Inspector.App.Tests.Documents;

/// <summary>
/// Runs posted UI work at once on the posting thread, one action at a time, standing in for Avalonia's dispatcher in
/// model tests. Counts posts, so tests can see that changes were coalesced.
/// </summary>
internal sealed class TestUi : IUiDispatcher
{
    private readonly Lock _gate = new();
    private int _posts;

    public int Posts => Volatile.Read(ref _posts);

    public void Post(Action action)
    {
        Interlocked.Increment(ref _posts);
        lock (_gate)
            action();
    }
}

/// <summary>Holds posted UI work until the test runs it, as a busy UI thread would between two frames.</summary>
internal sealed class QueuedUi : IUiDispatcher
{
    private readonly Queue<Action> _queue = new();

    public int Pending => _queue.Count;

    public void Post(Action action) => _queue.Enqueue(action);

    public void RunAll()
    {
        while (_queue.TryDequeue(out var action))
            action();
    }
}

/// <summary>
/// Forwards posted UI work to a dispatcher that can change: a test indexes documents with <see cref="TestUi"/>, then
/// hands later work to the headless UI thread before views bind to the results.
/// </summary>
internal sealed class SwitchableUi(IUiDispatcher target) : IUiDispatcher
{
    public IUiDispatcher Target { get; set; } = target;

    public void Post(Action action) => Target.Post(action);
}

internal static class Wait
{
    /// <summary>Polls until <paramref name="condition"/> holds; fails the test with <paramref name="what"/> on timeout.</summary>
    public static async Task UntilAsync(Func<bool> condition, string what, int timeoutMs = 15_000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
                throw new TimeoutException("Timed out waiting for " + what + ".");
            await Task.Delay(10);
        }
    }

    public static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
}
