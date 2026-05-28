using System;
using System.Threading;
using NUnit.Framework;
using NUnit.Framework.Interfaces;

// Apply at assembly scope so the action wraps every test in the assembly without each fixture
// having to opt in.
[assembly: NexNet.IntegrationTests.Infrastructure.TestProgressReporter]

namespace NexNet.IntegrationTests.Infrastructure;

/// <summary>
/// Assembly-level <see cref="ITestAction"/> that emits one progress line per N tests OR every
/// T seconds — whichever fires first — so CI logs show "test session still alive" feedback for
/// long-running suites (the integration suite has ~2800 cases and a flat `--logger minimal`
/// run otherwise prints nothing for several minutes).
/// </summary>
/// <remarks>
/// Output lines look like:
/// <code>
/// [progress] NexNet.IntegrationTests: 100 ran (100 passed, 0 failed), 12.4s elapsed
/// </code>
/// The line is written to <see cref="Console.Error"/>: the dotnet test console logger
/// suppresses <c>TestContext.Progress</c> output at <c>verbosity=minimal</c>, but stderr
/// passes through untouched. Tests are free to use TestContext.Out / Error themselves; this
/// reporter doesn't interfere with that.
/// </remarks>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false)]
internal sealed class TestProgressReporterAttribute : Attribute, ITestAction
{
    // Emit every N tests OR every T seconds, whichever comes first.
    private const int EveryNTests = 100;
    private static readonly TimeSpan EveryInterval = TimeSpan.FromSeconds(15);

    private static int _ran;
    private static int _failed;
    private static long _startTicks;
    private static long _lastEmitTicks;
    private static readonly object _gate = new();

    public ActionTargets Targets => ActionTargets.Test;

    public void BeforeTest(ITest test)
    {
        // Capture session-start the first time any test is about to run, and seed the last-
        // emit watermark with it so the time-cadence check doesn't fire spuriously on the
        // very first test (where lastEmit=0 makes any elapsed look huge).
        if (_startTicks == 0)
        {
            var now = Environment.TickCount64;
            if (Interlocked.CompareExchange(ref _startTicks, now, 0) == 0)
                Volatile.Write(ref _lastEmitTicks, now);
        }
    }

    public void AfterTest(ITest test)
    {
        var ran = Interlocked.Increment(ref _ran);
        var outcome = TestContext.CurrentContext.Result.Outcome.Status;
        if (outcome == TestStatus.Failed)
            Interlocked.Increment(ref _failed);

        var nowTicks = Environment.TickCount64;
        var elapsedSinceLastEmit = nowTicks - Volatile.Read(ref _lastEmitTicks);
        var hitTestCadence = (ran % EveryNTests) == 0;
        var hitTimeCadence = elapsedSinceLastEmit >= (long)EveryInterval.TotalMilliseconds;

        if (!hitTestCadence && !hitTimeCadence)
            return;

        // Single emitter to keep output lines coherent.
        lock (_gate)
        {
            if (Volatile.Read(ref _lastEmitTicks) > nowTicks - 100)
                return; // another thread just emitted

            var elapsed = TimeSpan.FromMilliseconds(nowTicks - Volatile.Read(ref _startTicks));
            var assemblyName = typeof(TestProgressReporterAttribute).Assembly.GetName().Name;
            var failed = Volatile.Read(ref _failed);
            var passed = ran - failed;
            // Stderr survives `console;verbosity=minimal`; TestContext.Progress does not.
            Console.Error.WriteLine(
                $"[progress] {assemblyName}: {ran} ran ({passed} passed, {failed} failed), {elapsed.TotalSeconds:F1}s elapsed");
            Volatile.Write(ref _lastEmitTicks, nowTicks);
        }
    }
}
