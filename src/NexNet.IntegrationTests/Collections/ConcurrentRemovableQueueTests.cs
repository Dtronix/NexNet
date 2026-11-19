using System.Collections.Concurrent;
using NexNet.Internals.Collections;

namespace NexNet.IntegrationTests.Collections;

internal class ConcurrentRemovableQueueTests
{
    [Test]
    public async Task EnqueueThenDequeue_SingleThread_FifoOrder()
    {
        var q = new ConcurrentRemovableQueue<int>();
        q.Enqueue(1);
        q.Enqueue(2);
        q.Enqueue(3);

        await Assert.That(q.TryDequeue(out var a)).IsTrue();
        await Assert.That(a).IsEqualTo(1);
        await Assert.That(q.TryDequeue(out var b)).IsTrue();
        await Assert.That(b).IsEqualTo(2);
        await Assert.That(q.TryDequeue(out var c)).IsTrue();
        await Assert.That(c).IsEqualTo(3);
        await Assert.That(q.TryDequeue(out _)).IsFalse();
        await Assert.That(q.Count).IsEqualTo(0);
    }

    [Test]
    public async Task TryDequeue_EmptyQueue_ReturnsFalse()
    {
        var q = new ConcurrentRemovableQueue<string>();
        await Assert.That(q.TryDequeue(out _)).IsFalse();
        await Assert.That(q.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Remove_ExistingItem_ReturnsTrue_AndDecrementsCount()
    {
        var q = new ConcurrentRemovableQueue<int>();
        q.Enqueue(10);
        q.Enqueue(11);

        await Assert.That(q.Remove(10)).IsTrue();
        await Assert.That(q.Count).IsEqualTo(1);
        await Assert.That(q.Remove(10)).IsFalse();
        await Assert.That(q.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Remove_NonExisting_ReturnsFalse()
    {
        var q = new ConcurrentRemovableQueue<int>();
        q.Enqueue(1);
        await Assert.That(q.Remove(2)).IsFalse();
        await Assert.That(q.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Remove_DuplicateValue_RemovesOneOccurrence()
    {
        var q = new ConcurrentRemovableQueue<int>();
        q.Enqueue(7);
        q.Enqueue(7);
        q.Enqueue(8);

        await Assert.That(q.Remove(7)).IsTrue();
        await Assert.That(q.Count).IsEqualTo(2);

        // One more 7 remains
        var got = new List<int>();
        foreach (var x in q) got.Add(x);
        await Assert.That(got.Count(v => v == 7)).IsEqualTo(1);
        await Assert.That(got.Count(v => v == 8)).IsEqualTo(1);
    }

    [Test]
    public async Task Count_MatchesAfterMixedOps()
    {
        var q = new ConcurrentRemovableQueue<int>();
        for (int i = 0; i < 50; i++) q.Enqueue(i);
        for (int i = 0; i < 20; i++) await Assert.That(q.Remove(i)).IsTrue();
        for (int i = 0; i < 15; i++) await Assert.That(q.TryDequeue(out _)).IsTrue();

        await Assert.That(q.Count).IsEqualTo(50 - 20 - 15);
    }

    [Test]
    public async Task Enumerator_IsStruct_NoAllocationsOnCreation()
    {
        var q = new ConcurrentRemovableQueue<int>();
        q.Enqueue(1);
        var e = q.GetEnumerator();
        await Assert.That(e.GetType().IsValueType).IsTrue();
        // Sanity: can iterate
        await Assert.That(e.MoveNext()).IsTrue();
        await Assert.That(e.Current).IsEqualTo(1);
    }

    [Test]
    public async Task Enumerator_SkipsNodes_RemovedBeforeEnumeration()
    {
        var q = new ConcurrentRemovableQueue<int>();
        for (int i = 0; i < 10; i++) q.Enqueue(i);
        for (int i = 0; i < 5; i++) await Assert.That(q.Remove(i)).IsTrue(); // remove first half

        var seen = new HashSet<int>();
        foreach (var x in q) seen.Add(x);

        // Should only see 5..9
        await Assert.That(seen.SetEquals(Enumerable.Range(5, 5))).IsTrue();
    }

    [Test]
    public async Task Enumeration_WithConcurrentRemovals_Completes_NoDuplicates()
    {
        var q = new ConcurrentRemovableQueue<int>();
        int n = 2000;
        for (int i = 0; i < n; i++) q.Enqueue(i);

        var start = new ManualResetEventSlim(false);
        var removed = 0;

        var remover = Task.Run(() =>
        {
            start.Wait();
            // remove many evens
            for (int i = 0; i < n; i += 2)
                if (q.Remove(i)) Interlocked.Increment(ref removed);
        });

        var enumerated = new ConcurrentDictionary<int, byte>();
        var enumeratorTask = Task.Run(() =>
        {
            start.Set();
            foreach (var x in q)
            {
                CallbackAssert.That(enumerated.TryAdd(x, 0)); // A duplicate observed during enumeration fails the WaitAll below.
            }
        });

        Task.WaitAll(remover, enumeratorTask);

        // All enumerated are within original domain
        await Assert.That(enumerated.Keys.All(v => v >= 0 && v < n)).IsTrue();
    }

    [Test]
    public async Task Enumeration_WithConcurrentDequeues_Completes_NoDuplicates()
    {
        var q = new ConcurrentRemovableQueue<int>();
        int n = 3000;
        for (int i = 0; i < n; i++) q.Enqueue(i);

        var start = new ManualResetEventSlim(false);

        var consumer = Task.Run(() =>
        {
            start.Wait();
            int c = 0;
            while (c < n / 2)
            {
                if (q.TryDequeue(out _)) c++;
            }
        });

        var enumerated = new ConcurrentDictionary<int, byte>();
        var enumeratorTask = Task.Run(() =>
        {
            start.Set();
            foreach (var x in q)
            {
                CallbackAssert.That(enumerated.TryAdd(x, 0));
            }
        });

        Task.WaitAll(consumer, enumeratorTask);
        // All enumerated values valid
        await Assert.That(enumerated.Keys.All(v => v >= 0 && v < n)).IsTrue();
    }

    [Test]
    public async Task Concurrent_ProducersConsumers_NoLostOrDuplicated()
    {
        var q = new ConcurrentRemovableQueue<int>();
        int producers = 4, consumers = 4, perProducer = 1000;
        int total = producers * perProducer;

        int produced = 0, consumed = 0;
        var start = new ManualResetEventSlim(false);

        var prodTasks = Enumerable.Range(0, producers).Select(p => Task.Run(() =>
        {
            start.Wait();
            for (int i = 0; i < perProducer; i++)
            {
                q.Enqueue((p << 20) | i);
                Interlocked.Increment(ref produced);
            }
        })).ToArray();

        var bag = new ConcurrentBag<int>();
        var consTasks = Enumerable.Range(0, consumers).Select(_ => Task.Run(() =>
        {
            start.Wait();
            while (Volatile.Read(ref consumed) < total)
            {
                if (q.TryDequeue(out var x))
                {
                    bag.Add(x);
                    Interlocked.Increment(ref consumed);
                }
            }
        })).ToArray();

        start.Set();
        Task.WaitAll(prodTasks);
        Task.WaitAll(consTasks);

        await Assert.That(produced).IsEqualTo(total);
        await Assert.That(consumed).IsEqualTo(total);

        // Verify uniqueness of values (no duplication)
        await Assert.That(bag.Count).IsEqualTo(total);
        await Assert.That(bag.Distinct().Count()).IsEqualTo(total);
        await Assert.That(q.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Concurrent_Remove_RandomItems_RemainingCountCorrect()
    {
        int initial = 4000;
        var q = new ConcurrentRemovableQueue<int>();
        for (int i = 0; i < initial; i++) q.Enqueue(i % 100); // many duplicates

        int successes = 0;
        RunConcurrently(workers: 8, () =>
        {
            for (int i = 0; i < 1000; i++)
            {
                int val = Random.Shared.Next(0, 100);
                if (q.Remove(val)) Interlocked.Increment(ref successes);
            }
        });

        await Assert.That(q.Count).IsEqualTo(initial - successes);
    }
    
    [Test]
    public async Task Concurrent_Remove_SameValue_AtMostOneSuccessPerOccurrence()
    {
        var q = new ConcurrentRemovableQueue<int>();
        q.Enqueue(42);
        q.Enqueue(42);
        q.Enqueue(42);

        int trueCount = 0;

        RunConcurrently(workers: 6, () =>
        {
            for (int i = 0; i < 10; i++)
            {
                if (q.Remove(42)) Interlocked.Increment(ref trueCount);
            }
        });

        await Assert.That(trueCount).IsEqualTo(3);
        await Assert.That(q.Count).IsEqualTo(0);
        await Assert.That(q.Remove(42)).IsFalse();
    }

    [Test]
    public async Task Interleaved_EnqueueAndRemove_CountRemainsConsistent()
    {
        var q = new ConcurrentRemovableQueue<int>();

        int enqs = 0, rms = 0;
        var start = new ManualResetEventSlim(false);

        var producer = Task.Run(() =>
        {
            start.Wait();
            for (int i = 0; i < 2000; i++)
            {
                q.Enqueue(i % 50);
                Interlocked.Increment(ref enqs);
            }
        });

        var remover = Task.Run(() =>
        {
            start.Wait();
            int attempts = 2500;
            while (attempts-- > 0)
            {
                if (q.Remove(Random.Shared.Next(0, 50))) Interlocked.Increment(ref rms);
            }
        });

        start.Set();
        Task.WaitAll(producer, remover);

        await Assert.That(q.Count).IsEqualTo(enqs - rms);
        await Assert.That(q.Count).IsGreaterThanOrEqualTo(0);
    }

    [Test]
    public async Task TryDequeue_SkipsTombstonedNodes()
    {
        var q = new ConcurrentRemovableQueue<int>();
        q.Enqueue(1);
        q.Enqueue(2);
        q.Enqueue(3);

        await Assert.That(q.Remove(1)).IsTrue();
        await Assert.That(q.Remove(2)).IsTrue();

        await Assert.That(q.TryDequeue(out var val)).IsTrue();
        await Assert.That(val).IsEqualTo(3);
        await Assert.That(q.TryDequeue(out _)).IsFalse();
    }
    
    [Test]
    public async Task RemoveAll_Then_TryDequeue_ReturnsFalse_CountZero()
    {
        var q = new ConcurrentRemovableQueue<int>();
        for (int i = 0; i < 10; i++) q.Enqueue(i);
        for (int i = 0; i < 10; i++) await Assert.That(q.Remove(i)).IsTrue();

        await Assert.That(q.TryDequeue(out _)).IsFalse();
        await Assert.That(q.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Enumerator_Reset_AllowsReiteration()
    {
        var q = new ConcurrentRemovableQueue<int>();
        q.Enqueue(5);
        q.Enqueue(6);
        var e = q.GetEnumerator();

        await Assert.That(e.MoveNext()).IsTrue();
        await Assert.That(e.Current).IsEqualTo(5);

        e.Reset();

        var list = new List<int>();
        while (e.MoveNext()) list.Add(e.Current);

        await Assert.That(list).IsEquivalentTo(new[] { 5, 6 }, CollectionOrdering.Matching);
    }
    
    [Test]
    public async Task Remove_ValueTypes_Works()
    {
        var q = new ConcurrentRemovableQueue<int>();
        q.Enqueue(100);
        await Assert.That(q.Remove(100)).IsTrue();
        await Assert.That(q.Remove(100)).IsFalse();
    }

    [Test]
    public async Task Remove_ReferenceTypes_Works()
    {
        var q = new ConcurrentRemovableQueue<string>();
        q.Enqueue("a");
        q.Enqueue("b");
        await Assert.That(q.Remove("a")).IsTrue();
        await Assert.That(q.Remove("a")).IsFalse();
        await Assert.That(q.TryDequeue(out var left)).IsTrue();
        await Assert.That(left).IsEqualTo("b");
    }
    
    [Test]
    public async Task Enumeration_Progresses_WhileHeadIsRemovedConcurrently()
    {
        var q = new ConcurrentRemovableQueue<int>();
        for (int i = 0; i < 5000; i++) q.Enqueue(i);

        var start = new ManualResetEventSlim(false);

        var killer = Task.Run(() =>
        {
            start.Wait();
            // aggressively remove current head values (best-effort)
            int attempts = 6000;
            while (attempts-- > 0)
            {
                q.TryDequeue(out _);
            }
        });

        int count = 0;
        var enumeratorTask = Task.Run(() =>
        {
            start.Set();
            foreach (var _ in q) count++;
        });

        Task.WaitAll(killer, enumeratorTask);
        // Enumeration finished (no hang) and saw some (possibly zero) items.
        await Assert.That(count).IsGreaterThanOrEqualTo(0);
        await Assert.That(q.Count).IsGreaterThanOrEqualTo(0);
    }

    [Test]
    public async Task Enumerator_StartedOnEmpty_DoesNotSeeFutureEnqueues()
    {
        var q = new ConcurrentRemovableQueue<int>();
        var e = q.GetEnumerator(); // snapshot when empty
        q.Enqueue(1);
        q.Enqueue(2);

        await Assert.That(e.MoveNext()).IsFalse(); // should not see items appended after creation
    }

    [Test]
    public async Task Enumeration_DuringTailAppends_IncludesAtLeastInitial_NoExceptions()
    {
        var q = new ConcurrentRemovableQueue<int>();
        for (int i = 0; i < 100; i++) q.Enqueue(i);

        var start = new ManualResetEventSlim(false);
        var enumerated = new HashSet<int>();

        var producer = Task.Run(() =>
        {
            start.Wait();
            for (int i = 100; i < 200; i++) q.Enqueue(i);
        });

        var consumer = Task.Run(() =>
        {
            start.Set();
            foreach (var x in q) enumerated.Add(x);
        });

        Task.WaitAll(producer, consumer);

        // Must at least contain the initial items; may or may not include tail appends.
        await Assert.That(enumerated.IsSupersetOf(Enumerable.Range(0, 100))).IsTrue();
    }

    [Test]
    public async Task CustomComparer_CaseInsensitive_Remove_Works()
    {
        var q = new ConcurrentRemovableQueue<string>(StringComparer.OrdinalIgnoreCase);
        q.Enqueue("FOO");

        await Assert.That(q.Remove("foo")).IsTrue();
        await Assert.That(q.Count).IsEqualTo(0);
        await Assert.That(q.TryDequeue(out _)).IsFalse();
    }

    [Test]
    public async Task Dequeue_OrderPreserved_AfterRemovingSome()
    {
        var q = new ConcurrentRemovableQueue<int>();
        for (int i = 0; i < 10; i++) q.Enqueue(i);
        // remove evens
        foreach (var i in Enumerable.Range(0, 10).Where(x => x % 2 == 0))
            await Assert.That(q.Remove(i)).IsTrue();

        var list = new List<int>();
        while (q.TryDequeue(out var v)) list.Add(v);
        await Assert.That(list).IsEquivalentTo(new[] { 1, 3, 5, 7, 9 }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task MultipleEnumerators_SeeConsistentViews_NoInterference()
    {
        var q = new ConcurrentRemovableQueue<int>();
        for (int i = 0; i < 1000; i++) q.Enqueue(i);

        var s1 = new HashSet<int>();
        var s2 = new HashSet<int>();

        foreach (var x in q) s1.Add(x);
        foreach (var x in q) s2.Add(x);

        await Assert.That(s1.SetEquals(Enumerable.Range(0, 1000))).IsTrue();
        await Assert.That(s2.SetEquals(Enumerable.Range(0, 1000))).IsTrue();
    }

    [Test]
    public async Task Enumerator_Reset_AfterMutations_ReflectsCurrentState()
    {
        var q = new ConcurrentRemovableQueue<int>();
        q.Enqueue(0);
        q.Enqueue(1);
        q.Enqueue(2);

        var e = q.GetEnumerator();
        await Assert.That(e.MoveNext()).IsTrue(); // 0
        await Assert.That(e.Current).IsEqualTo(0);

        // Mutate: remove head and add one at tail
        await Assert.That(q.Remove(0)).IsTrue();
        q.Enqueue(3);

        e.Reset();
        var list = new List<int>();
        while (e.MoveNext()) list.Add(e.Current);

        await Assert.That(list).IsEquivalentTo(new[] { 1, 2, 3 }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Remove_Tail_Then_Enqueue_Dequeue_Integrity()
    {
        var q = new ConcurrentRemovableQueue<int>();
        q.Enqueue(1);
        q.Enqueue(2);
        q.Enqueue(3);
        await Assert.That(q.Remove(3)).IsTrue();
        q.Enqueue(4);

        await Assert.That(q.TryDequeue(out var a)).IsTrue();
        await Assert.That(a).IsEqualTo(1);
        await Assert.That(q.TryDequeue(out var b)).IsTrue();
        await Assert.That(b).IsEqualTo(2);
        await Assert.That(q.TryDequeue(out var c)).IsTrue();
        await Assert.That(c).IsEqualTo(4);
        await Assert.That(q.TryDequeue(out _)).IsFalse();
    }

    [Test]
    public async Task Remove_Head_Then_Enqueue_PreservesOrder()
    {
        var q = new ConcurrentRemovableQueue<int>();
        q.Enqueue(1);
        q.Enqueue(2);
        q.Enqueue(3);
        await Assert.That(q.Remove(1)).IsTrue();
        q.Enqueue(4);

        var seen = new List<int>();
        while (q.TryDequeue(out var v)) seen.Add(v);
        await Assert.That(seen).IsEquivalentTo(new[] { 2, 3, 4 }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Remove_All_Then_EnqueueNew_Works()
    {
        var q = new ConcurrentRemovableQueue<int>();
        q.Enqueue(1);
        q.Enqueue(2);
        await Assert.That(q.Remove(1)).IsTrue();
        await Assert.That(q.Remove(2)).IsTrue();

        q.Enqueue(3);
        q.Enqueue(4);

        await Assert.That(q.TryDequeue(out var a)).IsTrue();
        await Assert.That(a).IsEqualTo(3);
        await Assert.That(q.TryDequeue(out var b)).IsTrue();
        await Assert.That(b).IsEqualTo(4);
        await Assert.That(q.TryDequeue(out _)).IsFalse();
    }

    [Test]
    public async Task ManyEnumerators_WithConcurrentRemovals_AllComplete_NoDuplicatesPerEnumerator()
    {
        var q = new ConcurrentRemovableQueue<int>();
        int n = 5000;
        for (int i = 0; i < n; i++) q.Enqueue(i);

        var start = new ManualResetEventSlim(false);

        var remover = Task.Run(() =>
        {
            start.Wait();
            for (int i = 1; i < n; i += 2) q.Remove(i);
        });

        var tasks = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            start.Wait();
            var seen = new HashSet<int>();
            foreach (var x in q)
            {
                // Asserts cannot be awaited here; a failure faults the task, which fails the WaitAll below.
                if (!seen.Add(x))
                    throw new InvalidOperationException($"Item {x} enumerated twice.");
                if (x < 0 || x > n - 1)
                    throw new InvalidOperationException($"Item {x} is out of range.");
            }
            return seen.Count;
        })).ToArray();

        start.Set();
        Task.WaitAll(tasks.Append(remover).ToArray());

        await Assert.That(tasks.All(t => t.Result >= 0)).IsTrue();
    }

    [Test]
    public async Task DequeueVsRemove_SingleItem_ExactlyOneWins()
    {
        var q = new ConcurrentRemovableQueue<int>();
        q.Enqueue(7);

        var go = new ManualResetEventSlim(false);
        bool removeSuccess = false;
        bool dequeueSuccess = false;

        var t1 = Task.Run(() =>
        {
            go.Wait();
            removeSuccess = q.Remove(7);
        });

        var t2 = Task.Run(() =>
        {
            go.Wait();
            dequeueSuccess = q.TryDequeue(out _);
        });

        go.Set();
        Task.WaitAll(t1, t2);

        await Assert.That(removeSuccess ^ dequeueSuccess).IsTrue(); // exactly one true
        await Assert.That(q.Count).IsEqualTo(0);
    }

    [Test]
    public async Task TryDequeue_SkipsManyTombstonedAtFront()
    {
        var q = new ConcurrentRemovableQueue<int>();
        for (int i = 0; i < 10; i++) q.Enqueue(i);
        for (int i = 0; i < 9; i++) await Assert.That(q.Remove(i)).IsTrue();

        await Assert.That(q.TryDequeue(out var v)).IsTrue();
        await Assert.That(v).IsEqualTo(9);
        await Assert.That(q.TryDequeue(out _)).IsFalse();
    }

    [Test]
    public async Task NullValues_NotSupported_ForReferenceTypes()
    {
        var q = new ConcurrentRemovableQueue<string>();
        Assert.ThrowsExactly<ArgumentNullException>(() => q.Enqueue(null!));
    }

    [Test]
    public async Task ValueTypeStruct_Remove_WorksWithDefaultEquality()
    {
        var q = new ConcurrentRemovableQueue<Pair>();
        var p = new Pair(1, 2);
        q.Enqueue(p);

        await Assert.That(q.Remove(new Pair(1, 2))).IsTrue();
        await Assert.That(q.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Remove_ExhaustiveDuplicates_FalseAfterExhaustion()
    {
        var q = new ConcurrentRemovableQueue<int>();
        q.Enqueue(7);
        q.Enqueue(7);
        q.Enqueue(7);

        await Assert.That(q.Remove(7)).IsTrue();
        await Assert.That(q.Remove(7)).IsTrue();
        await Assert.That(q.Remove(7)).IsTrue();
        await Assert.That(q.Remove(7)).IsFalse();
        await Assert.That(q.Count).IsEqualTo(0);
    }

    [Test]
    public async Task RandomConcurrentMix_CountEqualsEnumerationCount_NoExceptions()
    {
        var q = new ConcurrentRemovableQueue<int>();
        for (int i = 0; i < 1000; i++) q.Enqueue(i % 50);

        RunConcurrently(8, () =>
        {
            for (int i = 0; i < 2000; i++)
            {
                int op = Random.Shared.Next(3);
                int val = Random.Shared.Next(0, 100);
                switch (op)
                {
                    case 0: q.Enqueue(val); break;
                    case 1: q.Remove(val); break;
                    case 2: q.TryDequeue(out _); break;
                }
            }
        });

        // Count should match enumeration of live nodes.
        int enumerated = 0;
        foreach (var _ in q) enumerated++;

        await Assert.That(q.Count).IsEqualTo(enumerated);
    }

    [Test]
    public async Task Enumeration_DuringContinuousEnqueues_Completes()
    {
        var q = new ConcurrentRemovableQueue<int>();
        for (int i = 0; i < 500; i++) q.Enqueue(i);

        var start = new ManualResetEventSlim(false);

        var producer = Task.Run(() =>
        {
            start.Wait();
            for (int i = 500; i < 2500; i++) q.Enqueue(i);
        });

        int count = 0;
        var enumeratorTask = Task.Run(() =>
        {
            start.Set();
            foreach (var _ in q) Interlocked.Increment(ref count);
        });

        Task.WaitAll(producer, enumeratorTask);
        await Assert.That(count).IsGreaterThanOrEqualTo(500); // at least initial items were seen
    }

    [Test]
    public async Task Enumerator_Reset_AfterConcurrentRemovals_ReflectsRemaining()
    {
        var q = new ConcurrentRemovableQueue<int>();
        for (int i = 0; i < 10; i++) q.Enqueue(i);

        var e = q.GetEnumerator();

        var done = new ManualResetEventSlim(false);
        var remover = Task.Run(() =>
        {
            for (int i = 0; i < 5; i++) q.Remove(i);
            done.Set();
        });

        done.Wait();
        e.Reset();

        var seen = new List<int>();
        while (e.MoveNext()) seen.Add(e.Current);

        await Assert.That(seen).IsEquivalentTo(new[] { 5, 6, 7, 8, 9 }, CollectionOrdering.Matching);
        remover.Wait();
    }

    [Test]
    public async Task RemoveOdds_DequeueEvens_TotalProcessedEqualsInitial()
    {
        var q = new ConcurrentRemovableQueue<int>();
        int n = 2000;
        for (int i = 0; i < n; i++) q.Enqueue(i);

        int removed = 0, dequeued = 0;
        var start = new ManualResetEventSlim(false);

        var remover = Task.Run(() =>
        {
            start.Wait();
            for (int i = 1; i < n; i += 2)
                if (q.Remove(i)) Interlocked.Increment(ref removed);
        });

        var consumer = Task.Run(() =>
        {
            start.Wait();
            while (true)
            {
                if (q.TryDequeue(out _)) Interlocked.Increment(ref dequeued);
                else break;
            }
        });

        start.Set();
        Task.WaitAll(remover, consumer);

        await Assert.That(removed + dequeued).IsEqualTo(n);
        await Assert.That(q.Count).IsEqualTo(0);
    }

    [Test]
    public async Task MassiveConcurrentRemovals_SameValue_AllOccurrencesAccounted()
    {
        var q = new ConcurrentRemovableQueue<int>();
        int occurrences = 1000;
        for (int i = 0; i < occurrences; i++) q.Enqueue(1);

        int removed = 0, dequeued = 0;
        var start = new ManualResetEventSlim(false);

        var removers = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            start.Wait();
            int local = 0;
            for (;;)
            {
                if (q.Remove(1)) local++;
                else break; // stop when none left matching value
            }
            Interlocked.Add(ref removed, local);
        })).ToArray();

        var consumer = Task.Run(() =>
        {
            start.Wait();
            while (q.TryDequeue(out var v))
            {
                if (v == 1) Interlocked.Increment(ref dequeued);
            }
        });

        start.Set();
        Task.WaitAll(removers.Append(consumer).ToArray());

        await Assert.That(removed + dequeued).IsEqualTo(occurrences);
        await Assert.That(q.Count).IsEqualTo(0);
    }
    
    [Test]
    public async Task ConcurrentFuzzing_InvariantsHold()
    {
        var q = new ConcurrentRemovableQueue<int>();

        const int iterations = 200_000;
        int workers = Math.Min(Environment.ProcessorCount, 8);
        int basePerWorker = iterations / workers;
        int remainder = iterations % workers;

        // Operation counters (successful ops only)
        int enq = 0, deq = 0, rm = 0;

        // Unique id space for enqueues
        int nextId = 0;

        // Thread-local RNG (independent seeds)
        var rngLocal = new ThreadLocal<Random>(() =>
        {
            unchecked
            {
                int seed = Environment.TickCount ^ (Environment.CurrentManagedThreadId * 48611);
                return new Random(seed);
            }
        });

        var start = new ManualResetEventSlim(false);

        var tasks = Enumerable.Range(0, workers).Select(w => Task.Run(() =>
        {
            // Distribute rounding remainder
            int perWorker = basePerWorker + (w < remainder ? 1 : 0);

            start.Wait();

            var rng = rngLocal.Value!;
            for (int i = 0; i < perWorker; i++)
            {
                // 0..59 => Enqueue (60%)
                // 60..89 => Dequeue (30%)
                // 90..99 => Remove  (10%)
                int roll = rng.Next(100);

                if (roll < 60)
                {
                    // ENQUEUE (unique id)
                    int id = Interlocked.Increment(ref nextId);
                    q.Enqueue(id);
                    Interlocked.Increment(ref enq);
                }
                else if (roll < 90)
                {
                    // DEQUEUE
                    if (q.TryDequeue(out _))
                        Interlocked.Increment(ref deq);
                }
                else
                {
                    // REMOVE a random id that *might* have been enqueued
                    int hi = Volatile.Read(ref nextId);
                    if (hi > 0)
                    {
                        int target = rng.Next(1, hi + 1);
                        if (q.Remove(target))
                            Interlocked.Increment(ref rm);
                    }
                }

                // Light periodic traversal to exercise the enumerator under churn
                if ((i & 0x7FF) == 0) // ~ every 2048 iterations
                {
                    foreach (var _ in q) { /* no-op */ }
                }

                // Sanity: Count should never be negative
                if ((i & 0x3FF) == 0)
                {
                    CallbackAssert.That(q.Count >= 0);
                }
            }
        })).ToArray();

        start.Set();
        Task.WaitAll(tasks);

        // --- Postconditions (no concurrent writers now) ---
        // 1) Count equals number of items we can enumerate
        int enumeratedCount = 0;
        foreach (var _ in q) enumeratedCount++;
        await Assert.That(q.Count).IsEqualTo(enumeratedCount).Because("Count != enumerated size at end.");

        // 2) Conservation: enqueued == removed + dequeued + remaining
        await Assert.That(enq).IsEqualTo(rm + deq + q.Count).Because($"Conservation failed: enq={enq}, rm={rm}, deq={deq}, remaining={q.Count}");

        // 3) A second pass should match the first (enumerator stability once quiescent)
        int secondPass = 0;
        foreach (var _ in q) secondPass++;
        await Assert.That(secondPass).IsEqualTo(enumeratedCount);
    }
    
    private static void RunConcurrently(int workers, Action body)
    {
        var start = new ManualResetEventSlim(false);
        var tasks = Enumerable.Range(0, workers).Select(_ => Task.Run(() =>
        {
            start.Wait();
            body();
        })).ToArray();

        start.Set();
        Task.WaitAll(tasks);
    }
    
    private static long SumRange(int start, int count)
        => ((long)start + (start + count - 1)) * count / 2;

    private struct Pair
    {
        public int A;
        public int B;
        public Pair(int a, int b) { A = a; B = b; }
        public override bool Equals(object? obj) => obj is Pair p && p.A == A && p.B == B;
        public override int GetHashCode() => HashCode.Combine(A, B);
    }

}