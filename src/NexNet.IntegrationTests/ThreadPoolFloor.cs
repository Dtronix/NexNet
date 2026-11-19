namespace NexNet.IntegrationTests;

/// <summary>
/// Raises the thread pool's minimum worker count for the run. Tests run in parallel, and some of them (the in-memory
/// collection stress tests, handler gates) block pool threads with <c>Task.WaitAll</c> or wait handles. Past the minimum
/// the pool adds threads slowly, so socket continuations of the tests running next to them can stall for longer than
/// their hang guards allow.
/// </summary>
public static class ThreadPoolFloor
{
    [Before(HookType.Assembly)]
    public static void Raise()
    {
        ThreadPool.GetMinThreads(out var worker, out var io);
        var floor = Math.Max(worker, Environment.ProcessorCount * 4);
        ThreadPool.SetMinThreads(floor, io);
    }
}
