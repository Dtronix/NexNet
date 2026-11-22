using NexNet.Interop.Remote;

namespace NexNet.Interop.Tests;

/// <summary>
/// The forwarded-call bookkeeping of the remote driver (control protocol §7.3): a <c>script.forwardCancel</c> or a
/// dispose that arrives after the forwarded call completed does nothing (H28).
/// </summary>
public class ForwardedCallsTests
{
    [Test]
    public async Task Cancel_RunningCall_CancelsItsToken()
    {
        var calls = new ForwardedCalls();
        var token = calls.Start("1");

        await Assert.That(calls.Cancel("1")).IsTrue();
        await Assert.That(token.IsCancellationRequested).IsTrue();
        calls.Complete("1");
        await Assert.That(calls.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Cancel_AfterTheCallCompleted_DoesNothing()
    {
        var calls = new ForwardedCalls();
        var token = calls.Start("1");
        calls.Complete("1");

        await Assert.That(calls.Cancel("1")).IsFalse();
        calls.CancelAll();
        await Assert.That(token.IsCancellationRequested).IsFalse();
        await Assert.That(calls.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Cancel_SourceDisposedBetweenLookupAndCancel_DoesNotThrow()
    {
        // The race behind H28: the canceler found the source, then the call completed and disposed it.
        var source = new CancellationTokenSource();
        source.Dispose();

        await Assert.That(ForwardedCalls.TryCancel(source)).IsFalse();
    }

    [Test]
    public async Task CancelAll_RacingCompletions_NeverThrows()
    {
        var calls = new ForwardedCalls();
        for (var round = 0; round < 2_000; round++)
        {
            var ids = Enumerable.Range(0, 4).Select(i => round + ":" + i).ToArray();
            foreach (var id in ids)
                calls.Start(id);

            var cancel = Task.Run(() =>
            {
                calls.CancelAll();
                foreach (var id in ids)
                    calls.Cancel(id);
            });
            var complete = Task.Run(() =>
            {
                foreach (var id in ids)
                    calls.Complete(id);
            });
            await Task.WhenAll(cancel, complete);
        }

        await Assert.That(calls.Count).IsEqualTo(0);
    }
}
