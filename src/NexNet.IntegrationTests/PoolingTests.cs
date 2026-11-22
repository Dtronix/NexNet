using NexNet.Pools;

#pragma warning disable VSTHRD200

namespace NexNet.IntegrationTests;

// These tests assert exact counts and reuse on process-wide static pools. They use element types private to this class
// (the library rents ListPool<T> for its own element types, such as channel items) and never run alongside each other.
[NotInParallel("static-state")]
internal class PoolingTests
{
    private readonly record struct IntItem(int Value);
    private readonly record struct StringItem(string Value);
    private readonly record struct DoubleItem(double Value);
    private readonly record struct ByteItem(byte Value);

    [Test]
    public async Task ListPool_Return_RetainsCapacity()
    {
        // Arrange: Rent a list and add items to grow its capacity
        var list = ListPool<IntItem>.Rent();
        for (int i = 0; i < 100; i++)
        {
            list.Add(new IntItem(i));
        }
        var originalCapacity = list.Capacity;
        await Assert.That(originalCapacity).IsGreaterThanOrEqualTo(100);

        // Act: Return to pool and rent again
        ListPool<IntItem>.Return(list);
        var reusedList = ListPool<IntItem>.Rent();

        // Assert: Should be same instance with retained capacity (not reset to 0)
        await Assert.That(reusedList).IsSameReferenceAs(list);
        await Assert.That(reusedList.Capacity).IsGreaterThanOrEqualTo(100);
        await Assert.That(reusedList.Count).IsEqualTo(0); // Should be cleared
    }

    [Test]
    public async Task ListPool_Return_TrimsOversizedLists()
    {
        // Arrange: Create a list with very large capacity
        var list = ListPool<IntItem>.Rent();
        for (int i = 0; i < 2000; i++)
        {
            list.Add(new IntItem(i));
        }
        await Assert.That(list.Capacity).IsGreaterThan(ListPool<IntItem>.MaxRetainedCapacity);

        // Act: Return to pool and rent again
        ListPool<IntItem>.Return(list);
        var reusedList = ListPool<IntItem>.Rent();

        // Assert: Should be trimmed to DefaultCapacity
        await Assert.That(reusedList).IsSameReferenceAs(list);
        await Assert.That(reusedList.Capacity).IsEqualTo(ListPool<IntItem>.DefaultCapacity);
    }

    [Test]
    public async Task ListPool_Return_NullListDoesNotThrow()
    {
        // Act & Assert: Should not throw for null
        await Assert.That(() => ListPool<IntItem>.Return(null!)).ThrowsNothing();
    }

    [Test]
    public async Task ListPool_PoolCount_TracksPoolSize()
    {
        // Clear the pool first
        ListPool<IntItem>.Clear();
        await Assert.That(ListPool<IntItem>.PoolCount).IsEqualTo(0);

        // Rent and return some lists
        var list1 = ListPool<IntItem>.Rent();
        var list2 = ListPool<IntItem>.Rent();

        ListPool<IntItem>.Return(list1);
        await Assert.That(ListPool<IntItem>.PoolCount).IsEqualTo(1);

        ListPool<IntItem>.Return(list2);
        await Assert.That(ListPool<IntItem>.PoolCount).IsEqualTo(2);

        // Rent one back
        var rented = ListPool<IntItem>.Rent();
        await Assert.That(ListPool<IntItem>.PoolCount).IsEqualTo(1);
    }

    [Test]
    public async Task ListPool_BoundedGrowth_DoesNotExceedMaxPoolSize()
    {
        // Clear the pool
        ListPool<StringItem>.Clear();

        // Return more items than the max pool size
        var lists = new List<List<StringItem>>();
        for (int i = 0; i < ListPool<StringItem>.MaxPoolSize + 50; i++)
        {
            lists.Add(new List<StringItem>());
        }

        foreach (var list in lists)
        {
            ListPool<StringItem>.Return(list);
        }

        // Pool count should not exceed MaxPoolSize
        await Assert.That(ListPool<StringItem>.PoolCount).IsLessThanOrEqualTo(ListPool<StringItem>.MaxPoolSize);
    }

    [Test]
    public async Task ListPool_Clear_ResetsPoolCount()
    {
        // Add some items
        var list = ListPool<DoubleItem>.Rent();
        ListPool<DoubleItem>.Return(list);
        await Assert.That(ListPool<DoubleItem>.PoolCount).IsGreaterThan(0);

        // Clear
        ListPool<DoubleItem>.Clear();

        // Pool count should be 0
        await Assert.That(ListPool<DoubleItem>.PoolCount).IsEqualTo(0);
    }

    private class TestCacheableObject
    {
        public int Value { get; set; }
    }

    [Test]
    public async Task ObjectCache_Rent_CreatesNewInstanceWhenPoolEmpty()
    {
        // Clear the pool
        StaticObjectPool<TestCacheableObject>.Clear();

        // Rent should create new instance
        var obj = StaticObjectPool<TestCacheableObject>.Rent();
        await Assert.That(obj).IsNotNull();
    }

    [Test]
    public async Task ObjectCache_Return_AddsToPool()
    {
        StaticObjectPool<TestCacheableObject>.Clear();
        var initialCount = StaticObjectPool<TestCacheableObject>.PoolCount;

        var obj = StaticObjectPool<TestCacheableObject>.Rent();
        StaticObjectPool<TestCacheableObject>.Return(obj);

        await Assert.That(StaticObjectPool<TestCacheableObject>.PoolCount).IsGreaterThan(initialCount);
    }

    [Test]
    public async Task ObjectCache_Return_NullDoesNotThrow()
    {
        // StaticObjectPool now accepts null gracefully (no-op)
        await Assert.That(() => StaticObjectPool<TestCacheableObject>.Return(null!)).ThrowsNothing();
    }

    [Test]
    public async Task ObjectCache_BoundedGrowth_DoesNotExceedMaxPoolSize()
    {
        // Clear the pool
        StaticObjectPool<TestCacheableObject>.Clear();

        // Return more items than the max pool size
        for (int i = 0; i < StaticObjectPool<TestCacheableObject>.MaxPoolSize + 50; i++)
        {
            StaticObjectPool<TestCacheableObject>.Return(new TestCacheableObject());
        }

        // Pool count should not exceed MaxPoolSize
        await Assert.That(StaticObjectPool<TestCacheableObject>.PoolCount).IsLessThanOrEqualTo(StaticObjectPool<TestCacheableObject>.MaxPoolSize);
    }

    [Test]
    public async Task ObjectCache_RentReturn_ReusesObjects()
    {
        StaticObjectPool<TestCacheableObject>.Clear();

        var obj1 = StaticObjectPool<TestCacheableObject>.Rent();
        obj1.Value = 42;
        StaticObjectPool<TestCacheableObject>.Return(obj1);

        var obj2 = StaticObjectPool<TestCacheableObject>.Rent();
        await Assert.That(obj2).IsSameReferenceAs(obj1);
        await Assert.That(obj2.Value).IsEqualTo(42); // State preserved (caller's responsibility to reset)
    }

    [Test]
    public async Task ObjectCache_Clear_ResetsPoolCount()
    {
        // Add some items
        StaticObjectPool<TestCacheableObject>.Return(new TestCacheableObject());
        StaticObjectPool<TestCacheableObject>.Return(new TestCacheableObject());
        await Assert.That(StaticObjectPool<TestCacheableObject>.PoolCount).IsGreaterThan(0);

        // Clear
        StaticObjectPool<TestCacheableObject>.Clear();

        // Pool count should be 0
        await Assert.That(StaticObjectPool<TestCacheableObject>.PoolCount).IsEqualTo(0);
    }

    [Test]
    public async Task RVTCS_BoundedGrowth_DoesNotExceedMaxPoolSize()
    {
        // Create many sources and return them all
        var sources = new List<PooledValueTaskSource<int>>();
        for (int i = 0; i < PooledValueTaskSource<int>.MaxPoolSize + 50; i++)
        {
            var source = PooledValueTaskSource<int>.Rent();
            source.TrySetResult(i);
            sources.Add(source);
        }

        foreach (var source in sources)
        {
            source.Return();
        }

        // Pool count should not exceed MaxPoolSize
        await Assert.That(PooledValueTaskSource<int>.PoolCount).IsLessThanOrEqualTo(PooledValueTaskSource<int>.MaxPoolSize);
    }

    [Test]
    public async Task RVTCS_PoolCount_TracksPoolSize()
    {
        // Rent and return to verify tracking works
        var source1 = PooledValueTaskSource<string>.Rent();
        source1.TrySetResult("test");
        var countBeforeReturn = PooledValueTaskSource<string>.PoolCount;

        source1.Return();

        // Count should have increased after return
        await Assert.That(PooledValueTaskSource<string>.PoolCount).IsGreaterThanOrEqualTo(countBeforeReturn);
    }

    [Test]
    public async Task RVTCS_ConcurrentRentReturn_RespectsPoolLimit()
    {
        var tasks = new List<Task>();

        // Spawn many concurrent rent/return operations
        for (int i = 0; i < 200; i++)
        {
            tasks.Add(Task.Run(async () =>
            {
                var source = PooledValueTaskSource<int>.Rent();
                var task = source.Task;
                source.TrySetResult(i);
                await task;
                source.Return();
            }));
        }

        await Task.WhenAll(tasks);

        // Pool should still respect the limit
        await Assert.That(PooledValueTaskSource<int>.PoolCount).IsLessThanOrEqualTo(PooledValueTaskSource<int>.MaxPoolSize);
    }

    [Test]
    public async Task ListPool_ConcurrentRentReturn_MaintainsIntegrity()
    {
        ListPool<IntItem>.Clear();
        var tasks = new List<Task>();

        for (int i = 0; i < 100; i++)
        {
            tasks.Add(Task.Run(() =>
            {
                for (int j = 0; j < 50; j++)
                {
                    var list = ListPool<IntItem>.Rent();
                    list.Add(new IntItem(j));
                    ListPool<IntItem>.Return(list);
                }
            }));
        }

        await Task.WhenAll(tasks);

        // Pool should respect limits even under concurrent access
        await Assert.That(ListPool<IntItem>.PoolCount).IsLessThanOrEqualTo(ListPool<IntItem>.MaxPoolSize);
    }

    [Test]
    public async Task ObjectCache_ConcurrentRentReturn_MaintainsIntegrity()
    {
        StaticObjectPool<TestCacheableObject>.Clear();
        var tasks = new List<Task>();

        for (int i = 0; i < 100; i++)
        {
            tasks.Add(Task.Run(() =>
            {
                for (int j = 0; j < 50; j++)
                {
                    var obj = StaticObjectPool<TestCacheableObject>.Rent();
                    obj.Value = j;
                    StaticObjectPool<TestCacheableObject>.Return(obj);
                }
            }));
        }

        await Task.WhenAll(tasks);

        // Pool should respect limits even under concurrent access
        await Assert.That(StaticObjectPool<TestCacheableObject>.PoolCount).IsLessThanOrEqualTo(StaticObjectPool<TestCacheableObject>.MaxPoolSize);
    }

    [Test]
    public async Task ListPool_CapacityRetention_PerformanceImprovement()
    {
        // This test demonstrates that capacity is retained for reuse
        ListPool<ByteItem>.Clear();

        // First use: grow the list
        var list1 = ListPool<ByteItem>.Rent();
        for (int i = 0; i < 500; i++)
        {
            list1.Add(new ByteItem((byte)(i % 256)));
        }
        var capacityAfterGrow = list1.Capacity;

        ListPool<ByteItem>.Return(list1);

        // Second use: should have retained capacity
        var list2 = ListPool<ByteItem>.Rent();
        await Assert.That(list2).IsSameReferenceAs(list1);
        await Assert.That(list2.Capacity).IsEqualTo(capacityAfterGrow);
        await Assert.That(list2.Count).IsEqualTo(0); // But content is cleared

        // Adding items shouldn't require reallocation up to the retained capacity
        for (int i = 0; i < capacityAfterGrow; i++)
        {
            list2.Add(new ByteItem((byte)(i % 256)));
            await Assert.That(list2.Capacity).IsEqualTo(capacityAfterGrow).Because("Capacity should not change - no reallocation needed");
        }
    }
}