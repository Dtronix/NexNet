global using TUnit.Assertions.Enums;

// Tests run in parallel: each one gets its own ports (TestPorts) and UDS directory (BaseTests.NewUdsEndPoint), and
// nothing depends on static state. Exceptions are marked on the test or class:
// - [NotInParallel("stress")]: stress tests (Parallel.Invoke, Thread.Sleep, high volume), metrics, session-budget and
//   rate-limit tests; they run alongside other tests but never alongside each other.
// - [NotInParallel]: tests that assert on wall-clock time; they run on their own.
// The limit keeps socket-heavy tests from starving each other's thread-pool time, which would trip their timeouts.
[assembly: ParallelLimiter<NexNet.IntegrationTests.IntegrationParallelLimit>]
