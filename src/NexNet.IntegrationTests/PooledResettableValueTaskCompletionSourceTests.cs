using NexNet.Internals.Threading;
using NUnit.Framework;

#pragma warning disable VSTHRD200

namespace NexNet.IntegrationTests;

internal class PooledResettableValueTaskCompletionSourceTests
{
    [Test]
    public void Rent_CreatesNewInstance_WhenPoolIsEmpty()
    {
        var source = PooledResettableValueTaskCompletionSource<int>.Rent();

        Assert.That(source, Is.Not.Null);
    }

    [Test]
    public void Rent_ReusesInstance_AfterReturn()
    {
        var source1 = PooledResettableValueTaskCompletionSource<int>.Rent();
        source1.Return();

        var source2 = PooledResettableValueTaskCompletionSource<int>.Rent();

        Assert.That(source2, Is.SameAs(source1));
    }

    [Test]
    public void Return_ThrowsInvalidOperationException_WhenNotRented()
    {
        var source = PooledResettableValueTaskCompletionSource<int>.Rent();
        source.Return();

        Assert.Throws<InvalidOperationException>(() => source.Return());
    }

    [Test]
    public void TrySetResult_ThrowsInvalidOperationException_WhenNotRented()
    {
        var source = PooledResettableValueTaskCompletionSource<int>.Rent();
        source.Return();

        Assert.Throws<InvalidOperationException>(() => source.TrySetResult(42));
    }

    [Test]
    public void TrySetException_ThrowsInvalidOperationException_WhenNotRented()
    {
        var source = PooledResettableValueTaskCompletionSource<int>.Rent();
        source.Return();

        Assert.Throws<InvalidOperationException>(() => source.TrySetException(new Exception()));
    }

    [Test]
    public async Task TrySetResult_CompletesTask_WithCorrectValue()
    {
        var source = PooledResettableValueTaskCompletionSource<int>.Rent();
        var task = source.Task;

        source.TrySetResult(42);

        var result = await task;
        Assert.That(result, Is.EqualTo(42));
    }

    [Test]
    public void TrySetResult_ReturnsFalse_WhenAlreadyCompleted()
    {
        var source = PooledResettableValueTaskCompletionSource<int>.Rent();

        var first = source.TrySetResult(42);
        var second = source.TrySetResult(100);

        Assert.That(first, Is.True);
        Assert.That(second, Is.False);
    }

    [Test]
    public async Task TrySetException_CompletesTask_WithException()
    {
        var source = PooledResettableValueTaskCompletionSource<int>.Rent();
        var task = source.Task;
        var expectedException = new InvalidOperationException("Test exception");

        source.TrySetException(expectedException);

        var ex = Assert.ThrowsAsync<InvalidOperationException>(async () => await task);
        Assert.That(ex.Message, Is.EqualTo("Test exception"));
    }

    [Test]
    public void TrySetException_ReturnsFalse_WhenAlreadyCompleted()
    {
        var source = PooledResettableValueTaskCompletionSource<int>.Rent();

        var first = source.TrySetException(new Exception("First"));
        var second = source.TrySetException(new Exception("Second"));

        Assert.That(first, Is.True);
        Assert.That(second, Is.False);
    }

    [Test]
    public void TrySetResult_ReturnsFalse_AfterTrySetException()
    {
        var source = PooledResettableValueTaskCompletionSource<int>.Rent();

        source.TrySetException(new Exception());
        var result = source.TrySetResult(42);

        Assert.That(result, Is.False);
    }

    [Test]
    public void TrySetException_ReturnsFalse_AfterTrySetResult()
    {
        var source = PooledResettableValueTaskCompletionSource<int>.Rent();

        source.TrySetResult(42);
        var result = source.TrySetException(new Exception());

        Assert.That(result, Is.False);
    }

    [Test]
    public async Task Reset_AllowsReuse_AfterCompletion()
    {
        var source = PooledResettableValueTaskCompletionSource<int>.Rent();

        source.TrySetResult(42);
        await source.Task;

        source.Reset();

        var task = source.Task;
        source.TrySetResult(100);
        var result = await task;

        Assert.That(result, Is.EqualTo(100));
    }

    [Test]
    public async Task Return_ResetsState_ForReuse()
    {
        var source = PooledResettableValueTaskCompletionSource<int>.Rent();
        source.TrySetResult(42);
        await source.Task;

        source.Return();

        var reused = PooledResettableValueTaskCompletionSource<int>.Rent();
        Assert.That(reused, Is.SameAs(source));

        var task = reused.Task;
        reused.TrySetResult(100);
        var result = await task;

        Assert.That(result, Is.EqualTo(100));
    }

    [Test]
    public void PoolCount_IncreasesAfterReturn()
    {
        var initialCount = PooledResettableValueTaskCompletionSource<int>.PoolCount;

        var source = PooledResettableValueTaskCompletionSource<int>.Rent();
        source.Return();

        var newCount = PooledResettableValueTaskCompletionSource<int>.PoolCount;
        Assert.That(newCount, Is.GreaterThanOrEqualTo(initialCount));
    }

    [Test]
    public async Task Task_Property_ReturnsValidValueTask()
    {
        var source = PooledResettableValueTaskCompletionSource<int>.Rent();

        var task = source.Task;
        source.TrySetResult(42);

        Assert.That(task.IsCompleted, Is.True);
        var result = await task;
        Assert.That(result, Is.EqualTo(42));
    }
    

    [Test]
    public async Task SequentialUse_WithReset_WorksCorrectly()
    {
        var source = PooledResettableValueTaskCompletionSource<int>.Rent();

        // First use
        var task1 = source.Task;
        source.TrySetResult(42);
        var result1 = await task1;
        Assert.That(result1, Is.EqualTo(42));

        // Reset and second use
        source.Reset();
        var task2 = source.Task;
        source.TrySetResult(100);
        var result2 = await task2;
        Assert.That(result2, Is.EqualTo(100));

        // Reset and third use
        source.Reset();
        var task3 = source.Task;
        source.TrySetResult(200);
        var result3 = await task3;
        Assert.That(result3, Is.EqualTo(200));
    }

    [Test]
    public async Task SequentialUse_WithException_WorksCorrectly()
    {
        var source = PooledResettableValueTaskCompletionSource<int>.Rent();

        // First use with exception
        var task1 = source.Task;
        source.TrySetException(new InvalidOperationException("First"));
        var ex1 = Assert.ThrowsAsync<InvalidOperationException>(async () => await task1);
        Assert.That(ex1?.Message, Is.EqualTo("First"));

        // Reset and second use with success
        source.Reset();
        var task2 = source.Task;
        source.TrySetResult(42);
        var result = await task2;
        Assert.That(result, Is.EqualTo(42));

        // Reset and third use with exception again
        source.Reset();
        var task3 = source.Task;
        source.TrySetException(new ArgumentException("Third"));
        var ex3 = Assert.ThrowsAsync<ArgumentException>(async () => await task3);
        Assert.That(ex3?.Message, Is.EqualTo("Third"));
    }

    [Test]
    public void Task_IsNotCompleted_BeforeSetResult()
    {
        var source = PooledResettableValueTaskCompletionSource<int>.Rent();
        var task = source.Task;

        Assert.That(task.IsCompleted, Is.False);
    }

    [Test]
    public void Task_IsCompleted_AfterSetResult()
    {
        var source = PooledResettableValueTaskCompletionSource<int>.Rent();
        source.TrySetResult(42);
        var task = source.Task;

        Assert.That(task.IsCompleted, Is.True);
    }

    [Test]
    public void Task_IsCompleted_AfterSetException()
    {
        var source = PooledResettableValueTaskCompletionSource<int>.Rent();
        source.TrySetException(new Exception());
        var task = source.Task;

        Assert.That(task.IsCompleted, Is.True);
    }
    [Test]
    public async Task ConcurrentRentAndReturn_MaintainsPoolIntegrity()
    {
        var tasks = new List<Task>();

        for (int i = 0; i < 100; i++)
        {
            tasks.Add(Task.Run(async () =>
            {
                var source = PooledResettableValueTaskCompletionSource<int>.Rent();
                var task = source.Task;
                source.TrySetResult(i);
                await task;
                source.Return();
            }));
        }

        await Task.WhenAll(tasks);

        // Pool should have instances available
        Assert.That(PooledResettableValueTaskCompletionSource<int>.PoolCount, Is.GreaterThan(0));
    }

    [Test]
    public async Task Task_CompletedSynchronously_CanBeAwaited()
    {
        var source = PooledResettableValueTaskCompletionSource<int>.Rent();
        source.TrySetResult(42);
        var task = source.Task;

        Assert.That(task.IsCompleted, Is.True);
        var result = await task;
        Assert.That(result, Is.EqualTo(42));
    }


    [Test]
    public async Task ContinuationExecutes_AfterCompletion()
    {
        var source = PooledResettableValueTaskCompletionSource<int>.Rent();
        var task = source.Task;
        var continuationExecuted = false;

        // Attach continuation via async/await
        var continuationTask = Task.Run(async () =>
        {
            await task;
            continuationExecuted = true;
        });

        // Let the continuation register
        await Task.Delay(10);

        // Complete the source
        source.TrySetResult(42);

        // Wait for continuation to execute
        await continuationTask;

        Assert.That(continuationExecuted, Is.True);
    }

    [Test]
    public async Task AsyncCompletion_BeforeAwait_CompletesImmediately()
    {
        var source = PooledResettableValueTaskCompletionSource<int>.Rent();

        // Complete before getting task
        source.TrySetResult(42);

        var task = source.Task;

        // Should complete synchronously
        Assert.That(task.IsCompleted, Is.True);

        var result = await task;
        Assert.That(result, Is.EqualTo(42));
    }
}
