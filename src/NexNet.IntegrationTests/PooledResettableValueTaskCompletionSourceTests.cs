using NexNet.Internals.Threading;

#pragma warning disable VSTHRD200

namespace NexNet.IntegrationTests;

internal class PooledResettableValueTaskCompletionSourceTests
{
    [Test]
    public async Task Rent_CreatesNewInstance_WhenPoolIsEmpty()
    {
        var source = PooledResettableValueTaskCompletionSource<int>.Rent();

        await Assert.That(source).IsNotNull();
    }

    [Test]
    public async Task Rent_ReusesInstance_AfterReturn()
    {
        var source1 = PooledResettableValueTaskCompletionSource<int>.Rent();
        source1.Return();

        var source2 = PooledResettableValueTaskCompletionSource<int>.Rent();

        await Assert.That(source2).IsSameReferenceAs(source1);
    }

    [Test]
    public async Task Return_ThrowsInvalidOperationException_WhenNotRented()
    {
        var source = PooledResettableValueTaskCompletionSource<int>.Rent();
        source.Return();

        Assert.ThrowsExactly<InvalidOperationException>(() => source.Return());
    }

    [Test]
    public async Task TrySetResult_ThrowsInvalidOperationException_WhenNotRented()
    {
        var source = PooledResettableValueTaskCompletionSource<int>.Rent();
        source.Return();

        Assert.ThrowsExactly<InvalidOperationException>(() => source.TrySetResult(42));
    }

    [Test]
    public async Task TrySetException_ThrowsInvalidOperationException_WhenNotRented()
    {
        var source = PooledResettableValueTaskCompletionSource<int>.Rent();
        source.Return();

        Assert.ThrowsExactly<InvalidOperationException>(() => source.TrySetException(new Exception()));
    }

    [Test]
    public async Task TrySetResult_CompletesTask_WithCorrectValue()
    {
        var source = PooledResettableValueTaskCompletionSource<int>.Rent();
        var task = source.Task;

        source.TrySetResult(42);

        var result = await task;
        await Assert.That(result).IsEqualTo(42);
    }

    [Test]
    public async Task TrySetResult_ReturnsFalse_WhenAlreadyCompleted()
    {
        var source = PooledResettableValueTaskCompletionSource<int>.Rent();

        var first = source.TrySetResult(42);
        var second = source.TrySetResult(100);

        await Assert.That(first).IsTrue();
        await Assert.That(second).IsFalse();
    }

    [Test]
    public async Task TrySetException_CompletesTask_WithException()
    {
        var source = PooledResettableValueTaskCompletionSource<int>.Rent();
        var task = source.Task;
        var expectedException = new InvalidOperationException("Test exception");

        source.TrySetException(expectedException);

        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () => await task);
        await Assert.That(ex!.Message).IsEqualTo("Test exception");
    }

    [Test]
    public async Task TrySetException_ReturnsFalse_WhenAlreadyCompleted()
    {
        var source = PooledResettableValueTaskCompletionSource<int>.Rent();

        var first = source.TrySetException(new Exception("First"));
        var second = source.TrySetException(new Exception("Second"));

        await Assert.That(first).IsTrue();
        await Assert.That(second).IsFalse();
    }

    [Test]
    public async Task TrySetResult_ReturnsFalse_AfterTrySetException()
    {
        var source = PooledResettableValueTaskCompletionSource<int>.Rent();

        source.TrySetException(new Exception());
        var result = source.TrySetResult(42);

        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task TrySetException_ReturnsFalse_AfterTrySetResult()
    {
        var source = PooledResettableValueTaskCompletionSource<int>.Rent();

        source.TrySetResult(42);
        var result = source.TrySetException(new Exception());

        await Assert.That(result).IsFalse();
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

        await Assert.That(result).IsEqualTo(100);
    }

    [Test]
    public async Task Return_ResetsState_ForReuse()
    {
        var source = PooledResettableValueTaskCompletionSource<int>.Rent();
        source.TrySetResult(42);
        await source.Task;

        source.Return();

        var reused = PooledResettableValueTaskCompletionSource<int>.Rent();
        await Assert.That(reused).IsSameReferenceAs(source);

        var task = reused.Task;
        reused.TrySetResult(100);
        var result = await task;

        await Assert.That(result).IsEqualTo(100);
    }

    [Test]
    public async Task PoolCount_IncreasesAfterReturn()
    {
        var initialCount = PooledResettableValueTaskCompletionSource<int>.PoolCount;

        var source = PooledResettableValueTaskCompletionSource<int>.Rent();
        source.Return();

        var newCount = PooledResettableValueTaskCompletionSource<int>.PoolCount;
        await Assert.That(newCount).IsGreaterThanOrEqualTo(initialCount);
    }

    [Test]
    public async Task Task_Property_ReturnsValidValueTask()
    {
        var source = PooledResettableValueTaskCompletionSource<int>.Rent();

        var task = source.Task;
        source.TrySetResult(42);

        await Assert.That(task.IsCompleted).IsTrue();
        var result = await task;
        await Assert.That(result).IsEqualTo(42);
    }
    

    [Test]
    public async Task SequentialUse_WithReset_WorksCorrectly()
    {
        var source = PooledResettableValueTaskCompletionSource<int>.Rent();

        // First use
        var task1 = source.Task;
        source.TrySetResult(42);
        var result1 = await task1;
        await Assert.That(result1).IsEqualTo(42);

        // Reset and second use
        source.Reset();
        var task2 = source.Task;
        source.TrySetResult(100);
        var result2 = await task2;
        await Assert.That(result2).IsEqualTo(100);

        // Reset and third use
        source.Reset();
        var task3 = source.Task;
        source.TrySetResult(200);
        var result3 = await task3;
        await Assert.That(result3).IsEqualTo(200);
    }

    [Test]
    public async Task SequentialUse_WithException_WorksCorrectly()
    {
        var source = PooledResettableValueTaskCompletionSource<int>.Rent();

        // First use with exception
        var task1 = source.Task;
        source.TrySetException(new InvalidOperationException("First"));
        var ex1 = await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () => await task1);
        await Assert.That(ex1?.Message).IsEqualTo("First");

        // Reset and second use with success
        source.Reset();
        var task2 = source.Task;
        source.TrySetResult(42);
        var result = await task2;
        await Assert.That(result).IsEqualTo(42);

        // Reset and third use with exception again
        source.Reset();
        var task3 = source.Task;
        source.TrySetException(new ArgumentException("Third"));
        var ex3 = await Assert.ThrowsExactlyAsync<ArgumentException>(async () => await task3);
        await Assert.That(ex3?.Message).IsEqualTo("Third");
    }

    [Test]
    public async Task Task_IsNotCompleted_BeforeSetResult()
    {
        var source = PooledResettableValueTaskCompletionSource<int>.Rent();
        var task = source.Task;

        await Assert.That(task.IsCompleted).IsFalse();
    }

    [Test]
    public async Task Task_IsCompleted_AfterSetResult()
    {
        var source = PooledResettableValueTaskCompletionSource<int>.Rent();
        source.TrySetResult(42);
        var task = source.Task;

        await Assert.That(task.IsCompleted).IsTrue();
    }

    [Test]
    public async Task Task_IsCompleted_AfterSetException()
    {
        var source = PooledResettableValueTaskCompletionSource<int>.Rent();
        source.TrySetException(new Exception());
        var task = source.Task;

        await Assert.That(task.IsCompleted).IsTrue();
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
        await Assert.That(PooledResettableValueTaskCompletionSource<int>.PoolCount).IsGreaterThan(0);
    }

    [Test]
    public async Task Task_CompletedSynchronously_CanBeAwaited()
    {
        var source = PooledResettableValueTaskCompletionSource<int>.Rent();
        source.TrySetResult(42);
        var task = source.Task;

        await Assert.That(task.IsCompleted).IsTrue();
        var result = await task;
        await Assert.That(result).IsEqualTo(42);
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

        await Assert.That(continuationExecuted).IsTrue();
    }

    [Test]
    public async Task AsyncCompletion_BeforeAwait_CompletesImmediately()
    {
        var source = PooledResettableValueTaskCompletionSource<int>.Rent();

        // Complete before getting task
        source.TrySetResult(42);

        var task = source.Task;

        // Should complete synchronously
        await Assert.That(task.IsCompleted).IsTrue();

        var result = await task;
        await Assert.That(result).IsEqualTo(42);
    }
}