using NexNet.Internals;
using NUnit.Framework;

namespace NexNet.IntegrationTests;

internal class CountingResetAwaiterTests
{

    [Repeat(100)]
    [Test]
    public async Task FiresPreSet()
    {
        var tcs = new TaskCompletionSource();
        var resetEvent = new CountingResetAwaiter();
        resetEvent.TrySetResult();
        await Task.Run(async () =>
        {
            await resetEvent;
            tcs.SetResult();
        });

        await tcs.Task.Timeout(1);
    }

    [Repeat(100)]
    [Test]
    public async Task FiresPostSet()
    {
        var tcs = new TaskCompletionSource();
        var resetEvent = new CountingResetAwaiter();
        
        _ = Task.Run(async () =>
        {
            await resetEvent;
            tcs.SetResult();
        });

        await Task.Delay(5);
        resetEvent.TrySetResult();

        await tcs.Task.Timeout(1);
    }
    

}
