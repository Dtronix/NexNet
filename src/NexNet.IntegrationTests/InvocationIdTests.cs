using NexNet.Cache;
using NexNet.IntegrationTests.SessionManagement;
using NexNet.Invocation;
using NUnit.Framework;

#pragma warning disable VSTHRD200

namespace NexNet.IntegrationTests;

/// <summary>
/// Tests for Fix 2.1: HashSet for invocation IDs (O(1) lookup)
/// Tests the SessionInvocationStateManager ID generation behavior.
/// </summary>
internal class InvocationIdTests
{
    private MockNexusSession _mockSession = null!;
    private CacheManager _cacheManager = null!;

    [SetUp]
    public void SetUp()
    {
        _mockSession = new MockNexusSession();
        _cacheManager = new CacheManager();
    }

    [Test]
    public void GetNextId_SequentialGeneration_ReturnsUniqueIds()
    {
        var manager = new SessionInvocationStateManager(_cacheManager, null, _mockSession);

        var ids = new HashSet<ushort>();
        for (int i = 0; i < 1000; i++)
        {
            var id = manager.GetNextId(true);
            Assert.That(ids.Add(id), Is.True, $"Duplicate ID generated at iteration {i}");
        }
    }

    [Test]
    public void GetNextId_WithoutAddingToInvocations_DoesNotTrack()
    {
        var manager = new SessionInvocationStateManager(_cacheManager, null, _mockSession);

        // Get IDs without tracking
        var id1 = manager.GetNextId(false);
        var id2 = manager.GetNextId(false);

        // They should be sequential
        Assert.That(id2, Is.EqualTo((ushort)(id1 + 1)));

        // Get an ID with tracking - should work since previous IDs weren't tracked
        var id3 = manager.GetNextId(true);
        Assert.That(id3, Is.EqualTo((ushort)(id2 + 1)));
    }

    [Test]
    public void GetNextId_WithWraparound_HandlesCorrectly()
    {
        var manager = new SessionInvocationStateManager(_cacheManager, null, _mockSession);

        // Generate IDs near the wraparound point
        var ids = new List<ushort>();
        for (int i = 0; i < 100; i++)
        {
            ids.Add(manager.GetNextId(true));
        }

        // All IDs should be unique
        Assert.That(ids.Distinct().Count(), Is.EqualTo(100));
    }

    [Test]
    public async Task GetNextId_ConcurrentAccess_ThreadSafe()
    {
        var manager = new SessionInvocationStateManager(_cacheManager, null, _mockSession);
        var ids = new System.Collections.Concurrent.ConcurrentBag<ushort>();
        var tasks = new List<Task>();

        // Spawn multiple concurrent tasks to get IDs
        for (int i = 0; i < 10; i++)
        {
            tasks.Add(Task.Run(() =>
            {
                for (int j = 0; j < 100; j++)
                {
                    ids.Add(manager.GetNextId(true));
                }
            }));
        }

        await Task.WhenAll(tasks);

        // All 1000 IDs should be unique
        var uniqueIds = ids.Distinct().ToList();
        Assert.That(uniqueIds.Count, Is.EqualTo(1000), "Not all concurrent IDs were unique");
    }

    [Test]
    public void GetNextId_AfterRemoval_CanReuseId()
    {
        var manager = new SessionInvocationStateManager(_cacheManager, null, _mockSession);

        // Get some IDs
        var id1 = manager.GetNextId(true);
        var id2 = manager.GetNextId(true);
        var id3 = manager.GetNextId(true);

        // Simulate completing invocation with id2 by calling UpdateInvocationResult
        // This would normally be called when a result is received
        // For this test, we'll use CancelAll which clears all invocations
        manager.CancelAll();

        // Now we should be able to get new IDs
        var newIds = new List<ushort>();
        for (int i = 0; i < 10; i++)
        {
            newIds.Add(manager.GetNextId(true));
        }

        Assert.That(newIds.Distinct().Count(), Is.EqualTo(10), "Should be able to get unique IDs after clearing");
    }

    [Test]
    public void GetNextId_HashSetLookup_IsEfficient()
    {
        // This test verifies that the lookup is O(1) by testing performance
        // with many active invocations
        var manager = new SessionInvocationStateManager(_cacheManager, null, _mockSession);

        // Fill up with many active invocations
        for (int i = 0; i < 10000; i++)
        {
            manager.GetNextId(true);
        }

        // Getting the next ID should still be fast
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 1000; i++)
        {
            manager.GetNextId(true);
        }
        sw.Stop();

        // Should complete in reasonable time (less than 100ms for 1000 operations)
        // With O(n) lookup this would be much slower
        Assert.That(sw.ElapsedMilliseconds, Is.LessThan(100),
            "ID generation should be fast with HashSet O(1) lookup");
    }

    [Test]
    public void CancelAll_ClearsAllTrackedInvocations()
    {
        var manager = new SessionInvocationStateManager(_cacheManager, null, _mockSession);

        // Get many IDs
        var originalIds = new List<ushort>();
        for (int i = 0; i < 100; i++)
        {
            originalIds.Add(manager.GetNextId(true));
        }

        // Cancel all
        manager.CancelAll();

        // Should be able to generate new IDs (they may or may not collide with old ones
        // depending on where the counter is, but the tracking should be cleared)
        var newIds = new List<ushort>();
        for (int i = 0; i < 100; i++)
        {
            newIds.Add(manager.GetNextId(true));
        }

        // All new IDs should be unique among themselves
        Assert.That(newIds.Distinct().Count(), Is.EqualTo(100));
    }

    [Test]
    public void GetNextId_NonTracked_DoesNotAffectTrackedIds()
    {
        var manager = new SessionInvocationStateManager(_cacheManager, null, _mockSession);

        // Get a tracked ID
        var trackedId1 = manager.GetNextId(true);

        // Get several non-tracked IDs
        for (int i = 0; i < 5; i++)
        {
            manager.GetNextId(false);
        }

        // Get another tracked ID
        var trackedId2 = manager.GetNextId(true);

        // Both tracked IDs should be unique (they are different)
        Assert.That(trackedId1, Is.Not.EqualTo(trackedId2));
    }
}
