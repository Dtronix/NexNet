using NexNet.Internals;
using NUnit.Framework;

namespace NexNet.IntegrationTests;

[TestFixture]
internal class SessionBudgetTests
{
    [Test]
    public void TryReserve_IsAllOrNothing()
    {
        var budget = new SessionBudget(100);

        Assert.That(budget.TryReserve(60), Is.True);
        Assert.That(budget.TryReserve(50), Is.False);
        Assert.That(budget.Reserved, Is.EqualTo(60));
    }

    [Test]
    public async Task ReserveAsync_WaitsInOrderUntilReleased()
    {
        var budget = new SessionBudget(100);
        budget.TryReserve(100);

        var first = budget.ReserveAsync(60, CancellationToken.None).AsTask();
        var second = budget.ReserveAsync(10, CancellationToken.None).AsTask();
        Assert.That(first.IsCompleted || second.IsCompleted, Is.False);

        // 40 free: the first (60) does not fit yet, and the second (10) may not overtake it.
        budget.Release(40);
        await Task.Delay(20);
        Assert.That(first.IsCompleted || second.IsCompleted, Is.False);

        // 70 free: both fit, in order.
        budget.Release(30);
        Assert.That(await first.WaitAsync(TimeSpan.FromSeconds(1)), Is.True);
        Assert.That(await second.WaitAsync(TimeSpan.FromSeconds(1)), Is.True);
        Assert.That(budget.Reserved, Is.EqualTo(100));
    }

    [Test]
    public async Task ReserveAsync_Canceled_ReturnsFalseAndGivesUpItsPlace()
    {
        var budget = new SessionBudget(100);
        budget.TryReserve(100);
        using var cts = new CancellationTokenSource();

        var canceled = budget.ReserveAsync(80, cts.Token).AsTask();
        var next = budget.ReserveAsync(20, CancellationToken.None).AsTask();
        cts.Cancel();
        Assert.That(await canceled.WaitAsync(TimeSpan.FromSeconds(1)), Is.False);

        budget.Release(20);
        Assert.That(await next.WaitAsync(TimeSpan.FromSeconds(1)), Is.True);
        Assert.That(budget.Reserved, Is.EqualTo(100));
    }

    [Test]
    public async Task ReserveAsync_LargerThanTheLimit_ReturnsFalse()
    {
        var budget = new SessionBudget(100);
        Assert.That(await budget.ReserveAsync(101, CancellationToken.None), Is.False);
        Assert.That(budget.Reserved, Is.Zero);
    }
}
