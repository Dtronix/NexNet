using NexNet.Pools;
using NUnit.Framework;

#pragma warning disable VSTHRD200

namespace NexNet.IntegrationTests;

internal class PoolingTests
{
    [Test]
    public void ListPool_Return_RetainsCapacity()
    {
        // Arrange: Rent a list and add items to grow its capacity
        var list = ListPool<int>.Rent();
        for (int i = 0; i < 100; i++)
        {
            list.Add(i);
        }
        var originalCapacity = list.Capacity;
        Assert.That(originalCapacity, Is.GreaterThanOrEqualTo(100));

        // Act: Return to pool and rent again
        ListPool<int>.Return(list);
        var reusedList = ListPool<int>.Rent();

        // Assert: Should be same instance with retained capacity (not reset to 0)
        Assert.That(reusedList, Is.SameAs(list));
        Assert.That(reusedList.Capacity, Is.GreaterThanOrEqualTo(100));
        Assert.That(reusedList.Count, Is.EqualTo(0)); // Should be cleared
    }

    [Test]
    public void ListPool_Return_TrimsOversizedLists()
    {
        // Arrange: Create a list with very large capacity
        var list = ListPool<int>.Rent();
        for (int i = 0; i < 2000; i++)
        {
            list.Add(i);
        }
        Assert.That(list.Capacity, Is.GreaterThan(ListPool<int>.MaxRetainedCapacity));

        // Act: Return to pool and rent again
        ListPool<int>.Return(list);
        var reusedList = ListPool<int>.Rent();

        // Assert: Should be trimmed to DefaultCapacity
        Assert.That(reusedList, Is.SameAs(list));
        Assert.That(reusedList.Capacity, Is.EqualTo(ListPool<int>.DefaultCapacity));
    }

    [Test]
    public void ListPool_Return_NullListDoesNotThrow()
    {
        // Act & Assert: Should not throw for null
        Assert.DoesNotThrow(() => ListPool<int>.Return(null!));
    }

    [Test]
    public void ListPool_PoolCount_TracksPoolSize()
    {
        // Clear the pool first
        ListPool<int>.Clear();
        Assert.That(ListPool<int>.PoolCount, Is.EqualTo(0));

        // Rent and return some lists
        var list1 = ListPool<int>.Rent();
        var list2 = ListPool<int>.Rent();

        ListPool<int>.Return(list1);
        Assert.That(ListPool<int>.PoolCount, Is.EqualTo(1));

        ListPool<int>.Return(list2);
        Assert.That(ListPool<int>.PoolCount, Is.EqualTo(2));

        // Rent one back
        var rented = ListPool<int>.Rent();
        Assert.That(ListPool<int>.PoolCount, Is.EqualTo(1));
    }

    [Test]
    public void ListPool_BoundedGrowth_DoesNotExceedMaxPoolSize()
    {
        // Clear the pool
        ListPool<string>.Clear();

        // Return more items than the max pool size
        var lists = new List<List<string>>();
        for (int i = 0; i < ListPool<string>.MaxPoolSize + 50; i++)
        {
            lists.Add(new List<string>());
        }

        foreach (var list in lists)
        {
            ListPool<string>.Return(list);
        }

        // Pool count should not exceed MaxPoolSize
        Assert.That(ListPool<string>.PoolCount, Is.LessThanOrEqualTo(ListPool<string>.MaxPoolSize));
    }

    [Test]
    public void ListPool_Clear_ResetsPoolCount()
    {
        // Add some items
        var list = ListPool<double>.Rent();
        ListPool<double>.Return(list);
        Assert.That(ListPool<double>.PoolCount, Is.GreaterThan(0));

        // Clear
        ListPool<double>.Clear();

        // Pool count should be 0
        Assert.That(ListPool<double>.PoolCount, Is.EqualTo(0));
    }

    private class TestCacheableObject
    {
        public int Value { get; set; }
    }

    [Test]
    public void ObjectCache_Rent_CreatesNewInstanceWhenPoolEmpty()
    {
        // Clear the pool
        StaticObjectPool<TestCacheableObject>.Clear();

        // Rent should create new instance
        var obj = StaticObjectPool<TestCacheableObject>.Rent();
        Assert.That(obj, Is.Not.Null);
    }

    [Test]
    public void ObjectCache_Return_AddsToPool()
    {
        StaticObjectPool<TestCacheableObject>.Clear();
        var initialCount = StaticObjectPool<TestCacheableObject>.PoolCount;

        var obj = StaticObjectPool<TestCacheableObject>.Rent();
        StaticObjectPool<TestCacheableObject>.Return(obj);

        Assert.That(StaticObjectPool<TestCacheableObject>.PoolCount, Is.GreaterThan(initialCount));
    }

    [Test]
    public void ObjectCache_Return_NullDoesNotThrow()
    {
        // StaticObjectPool now accepts null gracefully (no-op)
        Assert.DoesNotThrow(() => StaticObjectPool<TestCacheableObject>.Return(null!));
    }

    [Test]
    public void ObjectCache_BoundedGrowth_DoesNotExceedMaxPoolSize()
    {
        // Clear the pool
        StaticObjectPool<TestCacheableObject>.Clear();

        // Return more items than the max pool size
        for (int i = 0; i < StaticObjectPool<TestCacheableObject>.MaxPoolSize + 50; i++)
        {
            StaticObjectPool<TestCacheableObject>.Return(new TestCacheableObject());
        }

        // Pool count should not exceed MaxPoolSize
        Assert.That(StaticObjectPool<TestCacheableObject>.PoolCount, Is.LessThanOrEqualTo(StaticObjectPool<TestCacheableObject>.MaxPoolSize));
    }

    [Test]
    public void ObjectCache_RentReturn_ReusesObjects()
    {
        StaticObjectPool<TestCacheableObject>.Clear();

        var obj1 = StaticObjectPool<TestCacheableObject>.Rent();
        obj1.Value = 42;
        StaticObjectPool<TestCacheableObject>.Return(obj1);

        var obj2 = StaticObjectPool<TestCacheableObject>.Rent();
        Assert.That(obj2, Is.SameAs(obj1));
        Assert.That(obj2.Value, Is.EqualTo(42)); // State preserved (caller's responsibility to reset)
    }

    [Test]
    public void ObjectCache_Clear_ResetsPoolCount()
    {
        // Add some items
        StaticObjectPool<TestCacheableObject>.Return(new TestCacheableObject());
        StaticObjectPool<TestCacheableObject>.Return(new TestCacheableObject());
        Assert.That(StaticObjectPool<TestCacheableObject>.PoolCount, Is.GreaterThan(0));

        // Clear
        StaticObjectPool<TestCacheableObject>.Clear();

        // Pool count should be 0
        Assert.That(StaticObjectPool<TestCacheableObject>.PoolCount, Is.EqualTo(0));
    }

    [Test]
    public void RVTCS_BoundedGrowth_DoesNotExceedMaxPoolSize()
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
        Assert.That(PooledValueTaskSource<int>.PoolCount,
            Is.LessThanOrEqualTo(PooledValueTaskSource<int>.MaxPoolSize));
    }

    [Test]
    public void RVTCS_PoolCount_TracksPoolSize()
    {
        // Rent and return to verify tracking works
        var source1 = PooledValueTaskSource<string>.Rent();
        source1.TrySetResult("test");
        var countBeforeReturn = PooledValueTaskSource<string>.PoolCount;

        source1.Return();

        // Count should have increased after return
        Assert.That(PooledValueTaskSource<string>.PoolCount,
            Is.GreaterThanOrEqualTo(countBeforeReturn));
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
        Assert.That(PooledValueTaskSource<int>.PoolCount,
            Is.LessThanOrEqualTo(PooledValueTaskSource<int>.MaxPoolSize));
    }

    [Test]
    public async Task ListPool_ConcurrentRentReturn_MaintainsIntegrity()
    {
        ListPool<int>.Clear();
        var tasks = new List<Task>();

        for (int i = 0; i < 100; i++)
        {
            tasks.Add(Task.Run(() =>
            {
                for (int j = 0; j < 50; j++)
                {
                    var list = ListPool<int>.Rent();
                    list.Add(j);
                    ListPool<int>.Return(list);
                }
            }));
        }

        await Task.WhenAll(tasks);

        // Pool should respect limits even under concurrent access
        Assert.That(ListPool<int>.PoolCount, Is.LessThanOrEqualTo(ListPool<int>.MaxPoolSize));
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
        Assert.That(StaticObjectPool<TestCacheableObject>.PoolCount,
            Is.LessThanOrEqualTo(StaticObjectPool<TestCacheableObject>.MaxPoolSize));
    }

    [Test]
    public void ListPool_CapacityRetention_PerformanceImprovement()
    {
        // This test demonstrates that capacity is retained for reuse
        ListPool<byte>.Clear();

        // First use: grow the list
        var list1 = ListPool<byte>.Rent();
        for (int i = 0; i < 500; i++)
        {
            list1.Add((byte)(i % 256));
        }
        var capacityAfterGrow = list1.Capacity;

        ListPool<byte>.Return(list1);

        // Second use: should have retained capacity
        var list2 = ListPool<byte>.Rent();
        Assert.That(list2, Is.SameAs(list1));
        Assert.That(list2.Capacity, Is.EqualTo(capacityAfterGrow));
        Assert.That(list2.Count, Is.EqualTo(0)); // But content is cleared

        // Adding items shouldn't require reallocation up to the retained capacity
        for (int i = 0; i < capacityAfterGrow; i++)
        {
            list2.Add((byte)(i % 256));
            Assert.That(list2.Capacity, Is.EqualTo(capacityAfterGrow),
                "Capacity should not change - no reallocation needed");
        }
    }
}
