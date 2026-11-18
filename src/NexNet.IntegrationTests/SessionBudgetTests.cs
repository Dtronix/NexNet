using NexNet.Internals;
using System.Threading.Tasks;

namespace NexNet.IntegrationTests;
internal class SessionBudgetTests
{
    [Test]
    public async Task TryReserve_IsAllOrNothing()
    {
        var budget = new SessionBudget(100);

        await Assert.That(budget.TryReserve(60)).IsTrue();
        await Assert.That(budget.TryReserve(50)).IsFalse();
        await Assert.That(budget.Reserved).IsEqualTo(60);
    }

    [Test]
    public async Task ReserveAsync_WaitsInOrderUntilReleased()
    {
        var budget = new SessionBudget(100);
        budget.TryReserve(100);

        var first = budget.ReserveAsync(60, CancellationToken.None).AsTask();
        var second = budget.ReserveAsync(10, CancellationToken.None).AsTask();
        await Assert.That(first.IsCompleted || second.IsCompleted).IsFalse();

        // 40 free: the first (60) does not fit yet, and the second (10) may not overtake it.
        budget.Release(40);
        await Task.Delay(20);
        await Assert.That(first.IsCompleted || second.IsCompleted).IsFalse();

        // 70 free: both fit, in order.
        budget.Release(30);
        await Assert.That(await first.WaitAsync(TimeSpan.FromSeconds(1))).IsTrue();
        await Assert.That(await second.WaitAsync(TimeSpan.FromSeconds(1))).IsTrue();
        await Assert.That(budget.Reserved).IsEqualTo(100);
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
        await Assert.That(await canceled.WaitAsync(TimeSpan.FromSeconds(1))).IsFalse();

        budget.Release(20);
        await Assert.That(await next.WaitAsync(TimeSpan.FromSeconds(1))).IsTrue();
        await Assert.That(budget.Reserved).IsEqualTo(100);
    }

    [Test]
    public async Task ReserveAsync_LargerThanTheLimit_ReturnsFalse()
    {
        var budget = new SessionBudget(100);
        await Assert.That(await budget.ReserveAsync(101, CancellationToken.None)).IsFalse();
        await Assert.That(budget.Reserved).IsZero();
    }
}