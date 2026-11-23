using NexNet.Inspector.App.Platform;
using TUnit.Assertions.Enums;

namespace NexNet.Inspector.App.Tests.Shell;

/// <summary>
/// The main window's extended-client-area recovery: a window message (re)starts a 250 ms debounce, then one check; Lost
/// re-applies the hint (false, then true) and re-checks after 0.5, 1, 2, 4 s, then every 10 s until Extended. A loss
/// is logged once, when it ends, naming its attempts and how long it lasted, or at dispose if it is still going on.
/// </summary>
public sealed class TitleBarRecoveryTests : IDisposable
{
    private const string Reapplied = "TITLE BAR: the extended client area was re-applied after ";
    private const string StillLost = "TITLE BAR: the extended client area was still lost when the App closed, after ";

    private FakeWindow _window = null!;
    private FakeScheduler _scheduler = null!;
    private List<string> _log = null!;

    [Before(HookType.Test)]
    public void SetUp()
    {
        _window = new FakeWindow();
        _scheduler = new FakeScheduler();
        _log = [];
    }

    public void Dispose() => _window.Dispose();

    private TitleBarRecovery Start() => TitleBarRecovery.Start(isWindows: true, () => _window, _scheduler, _log.Add)!;

    [Test]
    public async Task AnEventLeadsToOneCheckAfterTheDebounce()
    {
        using TitleBarRecovery recovery = Start();
        _window.Raise();
        _scheduler.Advance(TimeSpan.FromMilliseconds(249));
        await Assert.That(_window.Reads).IsZero().Because("not before the debounce");
        _scheduler.Advance(TimeSpan.FromMilliseconds(1));
        await Assert.That(_window.Reads).IsEqualTo(1);
        _scheduler.Advance(TimeSpan.FromMinutes(5));
        await Assert.That(_window.Reads).IsEqualTo(1).Because("no poll outside a loss");
    }

    [Test]
    public async Task ABurstOfEventsLeadsToOneCheck()
    {
        using TitleBarRecovery recovery = Start();
        for (int i = 0; i < 10; i++)
        {
            _window.Raise();
            _scheduler.Advance(TimeSpan.FromMilliseconds(100));
        }

        await Assert.That(_window.Reads).IsZero().Because("each event restarts the debounce");
        _scheduler.Advance(TimeSpan.FromMilliseconds(150));
        await Assert.That(_window.Reads).IsEqualTo(1);
    }

    [Test]
    public async Task ExtendedIsNotToggled()
    {
        using TitleBarRecovery recovery = Start();
        _window.Raise();
        _scheduler.Advance(TimeSpan.FromSeconds(1));
        using (Assert.Multiple())
        {
            await Assert.That(_window.Reads).IsEqualTo(1);
            await Assert.That(_window.Toggles).IsZero();
            await Assert.That(_log).IsEmpty();
        }
    }

    [Test]
    public async Task UnknownIsNotToggled()
    {
        _window.States.Enqueue(ExtendedClientState.Unknown);
        using TitleBarRecovery recovery = Start();
        _window.Raise();
        _scheduler.Advance(TimeSpan.FromMinutes(1));
        using (Assert.Multiple())
        {
            await Assert.That(_window.Reads).IsEqualTo(1);
            await Assert.That(_window.Toggles).IsZero();
            await Assert.That(_log).IsEmpty();
        }
    }

    [Test]
    public async Task ALossThatRecoversOnTheFirstRecheckLogsOneLine()
    {
        _window.States.Enqueue(ExtendedClientState.Lost);
        using TitleBarRecovery recovery = Start();
        _window.Raise();
        _scheduler.Advance(TimeSpan.FromMilliseconds(250));
        await Assert.That(_window.Toggles).IsEqualTo(1).Because("re-applied at the first check");
        await Assert.That(_log).IsEmpty().Because("nothing is written when a loss starts");
        _scheduler.Advance(TimeSpan.FromMilliseconds(500));
        await Assert.That(_window.Reads).IsEqualTo(2).Because("re-checked 0.5 s later");
        _scheduler.Advance(TimeSpan.FromMinutes(5));
        using (Assert.Multiple())
        {
            await Assert.That(_window.Reads).IsEqualTo(2).Because("Extended again: no more checks");
            await Assert.That(_window.Toggles).IsEqualTo(1);
            await Assert.That(_log).IsEquivalentTo(new[] { Reapplied + "1 attempt (0.5 s)" }, CollectionOrdering.Matching);
        }
    }

    [Test]
    public async Task StillLostRetriesAtTheBackoffNeverSooner()
    {
        for (int i = 0; i < 9; i++) _window.States.Enqueue(ExtendedClientState.Lost);
        using TitleBarRecovery recovery = Start();
        _window.Raise();
        _scheduler.Advance(TimeSpan.FromMilliseconds(250));

        // Each re-check, and the toggle when it is still Lost, comes exactly this long after the one before.
        double[] gaps = [0.5, 1, 2, 4, 10, 10, 10, 10];
        int reads = 1;
        foreach (double gap in gaps)
        {
            _scheduler.Advance(TimeSpan.FromSeconds(gap) - TimeSpan.FromMilliseconds(1));
            await Assert.That(_window.Reads).IsEqualTo(reads).Because($"not before {gap} s");
            _scheduler.Advance(TimeSpan.FromMilliseconds(1));
            await Assert.That(_window.Reads).IsEqualTo(++reads).Because($"at {gap} s");
            await Assert.That(_window.Toggles).IsEqualTo(reads).Because("every Lost read re-applies");
        }

        await Assert.That(_log).IsEmpty().Because("nothing is written while the loss goes on");
    }

    [Test]
    public async Task ALossThatTakesSeveralAttemptsLogsOneLine()
    {
        for (int i = 0; i < 3; i++) _window.States.Enqueue(ExtendedClientState.Lost);
        using TitleBarRecovery recovery = Start();
        _window.Raise();
        _scheduler.Advance(TimeSpan.FromMilliseconds(250 + 500 + 1000 + 2000));
        await Assert.That(_window.Reads).IsEqualTo(4).Because("Extended at the third re-check");
        _scheduler.Advance(TimeSpan.FromMinutes(10));
        using (Assert.Multiple())
        {
            await Assert.That(_window.Reads).IsEqualTo(4);
            await Assert.That(_window.Toggles).IsEqualTo(3);
            await Assert.That(_log).IsEquivalentTo(new[] { Reapplied + "3 attempts (3.5 s)" }, CollectionOrdering.Matching);
        }
    }

    [Test]
    public async Task ALongLossNamesItsAttemptsAndHowLongItLastedInHoursAndMinutes()
    {
        // 469 Lost reads: the check and 468 re-checks; Extended at the 469th re-check, 0.5 + 1 + 2 + 4 + 465 x 10 s later.
        for (int i = 0; i < 469; i++) _window.States.Enqueue(ExtendedClientState.Lost);
        using TitleBarRecovery recovery = Start();
        _window.Raise();
        _scheduler.Advance(TimeSpan.FromHours(2));
        using (Assert.Multiple())
        {
            await Assert.That(_window.Toggles).IsEqualTo(469);
            await Assert.That(_log).IsEquivalentTo(new[] { Reapplied + "469 attempts (1 h 17 min)" }, CollectionOrdering.Matching);
        }
    }

    [Test]
    public async Task DisposeDuringALossLogsOneLineWithItsAttemptsAndDuration()
    {
        for (int i = 0; i < 5; i++) _window.States.Enqueue(ExtendedClientState.Lost);
        TitleBarRecovery recovery = Start();
        _window.Raise();
        _scheduler.Advance(TimeSpan.FromMilliseconds(250 + 500 + 1000));
        await Assert.That(_log).IsEmpty().Because("still lost: nothing yet");
        recovery.Dispose();
        recovery.Dispose();
        _scheduler.Advance(TimeSpan.FromMinutes(1));
        using (Assert.Multiple())
        {
            await Assert.That(_window.Toggles).IsEqualTo(3);
            await Assert.That(_log).IsEquivalentTo(new[] { StillLost + "3 attempts (1.5 s)" }, CollectionOrdering.Matching).Because("one line, even when disposed twice");
        }
    }

    [Test]
    public async Task DisposeOutsideALossWritesNothing()
    {
        _window.States.Enqueue(ExtendedClientState.Lost);
        TitleBarRecovery recovery = Start();
        _window.Raise();
        _scheduler.Advance(TimeSpan.FromSeconds(1));
        recovery.Dispose();
        await Assert.That(_log).IsEquivalentTo(new[] { Reapplied + "1 attempt (0.5 s)" }, CollectionOrdering.Matching).Because("only the end of the loss");
    }

    [Test]
    public async Task DisposeDuringALossDoesNotThrowWhenTheLogIsClosing()
    {
        _window.States.Enqueue(ExtendedClientState.Lost);
        TitleBarRecovery recovery = TitleBarRecovery.Start(isWindows: true, () => _window, _scheduler, static _ => throw new ObjectDisposedException("log"))!;
        _window.Raise();
        _scheduler.Advance(TimeSpan.FromMilliseconds(250));
        await Assert.That(recovery.Dispose).ThrowsNothing();
        await Assert.That(_window.HasListeners).IsFalse().Because("it still lets go of the window's events");
    }

    [Test]
    public async Task EventsDuringALossNeverToggleBetweenRetries()
    {
        for (int i = 0; i < 5; i++) _window.States.Enqueue(ExtendedClientState.Lost);
        using TitleBarRecovery recovery = Start();
        _window.Raise();
        _scheduler.Advance(TimeSpan.FromMilliseconds(250));
        await Assert.That(_window.Toggles).IsEqualTo(1);
        for (int i = 0; i < 4; i++)
        {
            _window.Raise();
            _scheduler.Advance(TimeSpan.FromMilliseconds(100));
        }

        await Assert.That(_window.Toggles).IsEqualTo(1).Because("a retry is pending");
    }

    [Test]
    public async Task ALaterLossIsANewEpisodeWithItsOwnLine()
    {
        _window.States.Enqueue(ExtendedClientState.Lost);
        using TitleBarRecovery recovery = Start();
        _window.Raise();
        _scheduler.Advance(TimeSpan.FromSeconds(1));
        _window.States.Enqueue(ExtendedClientState.Lost);
        _window.States.Enqueue(ExtendedClientState.Lost);
        _window.Raise();
        _scheduler.Advance(TimeSpan.FromSeconds(3));
        using (Assert.Multiple())
        {
            await Assert.That(_window.Toggles).IsEqualTo(3);
            await Assert.That(_log).IsEquivalentTo(new[] { Reapplied + "1 attempt (0.5 s)", Reapplied + "2 attempts (1.5 s)" }, CollectionOrdering.Matching).Because("the count and clock start again");
        }
    }

    [Test]
    public async Task AfterDisposeNoEventLeadsToACheckOrAToggle()
    {
        _window.States.Enqueue(ExtendedClientState.Lost);
        TitleBarRecovery recovery = Start();
        _window.Raise();
        recovery.Dispose();
        _window.Raise();
        _scheduler.Advance(TimeSpan.FromMinutes(1));
        using (Assert.Multiple())
        {
            await Assert.That(_window.Reads).IsZero();
            await Assert.That(_window.Toggles).IsZero();
            await Assert.That(_window.HasListeners).IsFalse().Because("it lets go of the window's events");
            await Assert.That(_scheduler.Pending).IsZero().Because("its pending check is cancelled");
        }
    }

    [Test]
    public async Task AfterDisposeARetryInFlightStops()
    {
        for (int i = 0; i < 5; i++) _window.States.Enqueue(ExtendedClientState.Lost);
        TitleBarRecovery recovery = Start();
        _window.Raise();
        _scheduler.Advance(TimeSpan.FromMilliseconds(250));
        recovery.Dispose();
        _scheduler.Advance(TimeSpan.FromMinutes(1));
        await Assert.That(_window.Toggles).IsEqualTo(1);
    }

    [Test]
    public async Task OffWindowsStartingDoesNothing()
    {
        bool made = false;
        TitleBarRecovery? recovery = TitleBarRecovery.Start(isWindows: false, () =>
        {
            made = true;
            return _window;
        }, _scheduler, _log.Add);
        using (Assert.Multiple())
        {
            await Assert.That(recovery).IsNull();
            await Assert.That(made).IsFalse().Because("no window seam (hook, P/Invoke) is made");
            await Assert.That(_scheduler.Scheduled).IsZero().Because("no timer");
        }
    }

    /// <summary>Reads the queued states (Extended once they run out), counts toggles, raises the window's messages.</summary>
    private sealed class FakeWindow : ITitleBarWindow
    {
        public Queue<ExtendedClientState> States { get; } = new();

        public int Reads { get; private set; }

        public int Toggles { get; private set; }

        public bool HasListeners => Changed is not null;

        public event EventHandler? Changed;

        public ExtendedClientState Read()
        {
            Reads++;
            return States.TryDequeue(out ExtendedClientState s) ? s : ExtendedClientState.Extended;
        }

        public void Reapply() => Toggles++;

        public void Raise() => Changed?.Invoke(this, EventArgs.Empty);

        public void Dispose()
        {
        }
    }

    /// <summary>A manual clock: work runs only when <see cref="Advance"/> passes its due time, in due order.</summary>
    private sealed class FakeScheduler : ITitleBarScheduler
    {
        private readonly List<Item> _items = [];
        private TimeSpan _now;

        public int Scheduled { get; private set; }

        public int Pending => _items.Count(i => !i.Cancelled);

        /// <summary>The manual clock's time, for the recovery's loss duration.</summary>
        public TimeSpan Now => _now;

        public IDisposable Schedule(TimeSpan delay, Action action)
        {
            Scheduled++;
            Item item = new(_now + delay, action);
            _items.Add(item);
            return item;
        }

        public void Advance(TimeSpan by)
        {
            TimeSpan until = _now + by;
            while (_items.Where(i => !i.Cancelled && i.Due <= until).OrderBy(i => i.Due).FirstOrDefault() is { } next)
            {
                _now = next.Due;
                _items.Remove(next);
                next.Action();
            }

            _items.RemoveAll(i => i.Cancelled);
            _now = until;
        }

        private sealed class Item(TimeSpan due, Action action) : IDisposable
        {
            public TimeSpan Due { get; } = due;

            public Action Action { get; } = action;

            public bool Cancelled { get; private set; }

            public void Dispose() => Cancelled = true;
        }
    }
}
