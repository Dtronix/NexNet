using System;
using System.Threading;
using NUnit.Framework;
using NUnit.Framework.Interfaces;

// Apply at assembly scope so the action wraps every test in the assembly without each fixture
// having to opt in.
[assembly: NexNet.Testing.Tests.Infrastructure.TestProgressReporter]

namespace NexNet.Testing.Tests.Infrastructure;

/// <summary>
/// Assembly-level <see cref="ITestAction"/> that emits one progress line per N tests OR every
/// T seconds — whichever fires first. See the integration-tests copy of this class for full
/// rationale; this file is a duplicate per-assembly because NUnit assembly-level actions are
/// not transitively imported across project references.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false)]
internal sealed class TestProgressReporterAttribute : Attribute, ITestAction
{
    private const int EveryNTests = 25;
    private static readonly TimeSpan EveryInterval = TimeSpan.FromSeconds(10);

    private static int _ran;
    private static int _failed;
    private static long _startTicks;
    private static long _lastEmitTicks;
    private static readonly object _gate = new();

    public ActionTargets Targets => ActionTargets.Test;

    public void BeforeTest(ITest test)
    {
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

        lock (_gate)
        {
            if (Volatile.Read(ref _lastEmitTicks) > nowTicks - 100)
                return;

            var elapsed = TimeSpan.FromMilliseconds(nowTicks - Volatile.Read(ref _startTicks));
            var assemblyName = typeof(TestProgressReporterAttribute).Assembly.GetName().Name;
            var failed = Volatile.Read(ref _failed);
            var passed = ran - failed;
            // Write to raw stderr so the dotnet test console logger doesn't filter it out at
            // `verbosity=minimal` (TestContext.Progress is filtered too at that level).
            Console.Error.WriteLine(
                $"[progress] {assemblyName}: {ran} ran ({passed} passed, {failed} failed), {elapsed.TotalSeconds:F1}s elapsed");
            Volatile.Write(ref _lastEmitTicks, nowTicks);
        }
    }
}
