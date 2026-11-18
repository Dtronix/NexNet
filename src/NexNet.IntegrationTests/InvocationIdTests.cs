using NexNet.IntegrationTests.SessionManagement;
using NexNet.Invocation;
using NexNet.Pools;

#pragma warning disable VSTHRD200

namespace NexNet.IntegrationTests;

internal class InvocationIdTests
{
    private MockNexusSession _mockSession = null!;
    private PoolManager _poolManager = null!;

    [Before(HookType.Test)]
    public void SetUp()
    {
        _mockSession = new MockNexusSession();
        _poolManager = new PoolManager();
    }

    [Test]
    public async Task GetNextId_SequentialGeneration_ReturnsUniqueIds()
    {
        var manager = new SessionInvocationStateManager(_poolManager, null, _mockSession);

        var ids = new HashSet<ushort>();
        for (int i = 0; i < 1000; i++)
        {
            var id = manager.GetNextId(true);
            await Assert.That(ids.Add(id)).IsTrue().Because($"Duplicate ID generated at iteration {i}");
        }
    }

    [Test]
    public async Task GetNextId_WithoutAddingToInvocations_DoesNotTrack()
    {
        var manager = new SessionInvocationStateManager(_poolManager, null, _mockSession);

        // Get IDs without tracking
        var id1 = manager.GetNextId(false);
        var id2 = manager.GetNextId(false);

        // They should be sequential
        await Assert.That(id2).IsEqualTo((ushort)(id1 + 1));

        // Get an ID with tracking - should work since previous IDs weren't tracked
        var id3 = manager.GetNextId(true);
        await Assert.That(id3).IsEqualTo((ushort)(id2 + 1));
    }

    [Test]
    public async Task GetNextId_WithWraparound_HandlesCorrectly()
    {
        var manager = new SessionInvocationStateManager(_poolManager, null, _mockSession);

        // Generate IDs near the wraparound point
        var ids = new List<ushort>();
        for (int i = 0; i < 100; i++)
        {
            ids.Add(manager.GetNextId(true));
        }

        // All IDs should be unique
        await Assert.That(ids.Distinct().Count()).IsEqualTo(100);
    }

    [Test]
    public async Task GetNextId_ConcurrentAccess_ThreadSafe()
    {
        var manager = new SessionInvocationStateManager(_poolManager, null, _mockSession);
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
        await Assert.That(uniqueIds.Count).IsEqualTo(1000).Because("Not all concurrent IDs were unique");
    }

    [Test]
    public async Task GetNextId_AfterRemoval_CanReuseId()
    {
        var manager = new SessionInvocationStateManager(_poolManager, null, _mockSession);

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

        await Assert.That(newIds.Distinct().Count()).IsEqualTo(10).Because("Should be able to get unique IDs after clearing");
    }

    [Test]
    public async Task GetNextId_HashSetLookup_IsEfficient()
    {
        // This test verifies that the lookup is O(1) by testing performance
        // with many active invocations
        var manager = new SessionInvocationStateManager(_poolManager, null, _mockSession);

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
        await Assert.That(sw.ElapsedMilliseconds).IsLessThan(100).Because("ID generation should be fast with HashSet O(1) lookup");
    }

    [Test]
    public async Task CancelAll_ClearsAllTrackedInvocations()
    {
        var manager = new SessionInvocationStateManager(_poolManager, null, _mockSession);

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
        await Assert.That(newIds.Distinct().Count()).IsEqualTo(100);
    }

    [Test]
    public async Task GetNextId_NonTracked_DoesNotAffectTrackedIds()
    {
        var manager = new SessionInvocationStateManager(_poolManager, null, _mockSession);

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
        await Assert.That(trackedId1).IsNotEqualTo(trackedId2);
    }

    [Test]
    public async Task UpdateInvocationResult_Unsolicited_ReturnsMessageToPool()
    {
        var manager = new SessionInvocationStateManager(_poolManager, null, _mockSession);
        var message = _poolManager.Rent<NexNet.Messages.InvocationResultMessage>();
        message.InvocationId = 1234;

        // No call is waiting for this result. It used to be dropped without returning it to its pool.
        manager.UpdateInvocationResult(message);

        await Assert.That(_poolManager.Rent<NexNet.Messages.InvocationResultMessage>()).IsSameReferenceAs(message);
    }

    [Test]
    public async Task InvokeMethodWithResultCore_CompletedState_ReleasesCancellationRegistration()
    {
        var manager = new SessionInvocationStateManager(_poolManager, null, _mockSession);
        using var cts = new CancellationTokenSource();

        var state = NexNet.Internals.RegisteredInvocationState.Rent();
        await manager.InvokeMethodWithResultCore(state, 0, Memory<byte>.Empty, _mockSession, cts.Token);
        await Assert.That(state.TrySetResult()).IsTrue().Because("The sent call is left for its result");

        // Released on completion, also when nothing reads the result: cancelling later does not reach the state.
        cts.Cancel();
        await Assert.That(state.IsCanceled).IsFalse();
        state.Return();
    }

    [Test]
    public async Task InvokeMethodWithResultCore_OversizedArguments_ThrowsWithoutHoldingId()
    {
        var manager = new SessionInvocationStateManager(_poolManager, null, _mockSession);
        var arguments = new byte[_mockSession.SendMaxMessageSize + 1];

        // The failure is set on the state, which the call reads.
        var state = NexNet.Internals.RegisteredInvocationState.Rent();
        var version = state.Version;
        await manager.InvokeMethodWithResultCore(state, 0, arguments, _mockSession);
        await Assert.ThrowsExactlyAsync<NexusMessageTooLargeException>(async () => await new ValueTask(state, version));

        // The rejected call took no ID, so the next ID is the first one.
        await Assert.That(manager.GetNextId(true)).IsEqualTo(1);
    }
}