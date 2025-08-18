using System.Collections.Concurrent;
using NexNet.Internals.Collections;
using NUnit.Framework;

namespace NexNet.IntegrationTests.Collections;

internal class ConcurrentRemovableQueueTests
{
    [Test]
    public void EnqueueThenDequeue_SingleThread_FifoOrder()
    {
        var q = new ConcurrentRemovableQueue<int>();
        q.Enqueue(1);
        q.Enqueue(2);
        q.Enqueue(3);

        Assert.That(q.TryDequeue(out var a), Is.True);
        Assert.That(a, Is.EqualTo(1));
        Assert.That(q.TryDequeue(out var b), Is.True);
        Assert.That(b, Is.EqualTo(2));
        Assert.That(q.TryDequeue(out var c), Is.True);
        Assert.That(c, Is.EqualTo(3));
        Assert.That(q.TryDequeue(out _), Is.False);
        Assert.That(q.Count, Is.EqualTo(0));
    }

    [Test]
    public void TryDequeue_EmptyQueue_ReturnsFalse()
    {
        var q = new ConcurrentRemovableQueue<string>();
        Assert.That(q.TryDequeue(out _), Is.False);
        Assert.That(q.Count, Is.EqualTo(0));
    }

    [Test]
    public void Remove_ExistingItem_ReturnsTrue_AndDecrementsCount()
    {
        var q = new ConcurrentRemovableQueue<int>();
        q.Enqueue(10);
        q.Enqueue(11);

        Assert.That(q.Remove(10), Is.True);
        Assert.That(q.Count, Is.EqualTo(1));
        Assert.That(q.Remove(10), Is.False);
        Assert.That(q.Count, Is.EqualTo(1));
    }

    [Test]
    public void Remove_NonExisting_ReturnsFalse()
    {
        var q = new ConcurrentRemovableQueue<int>();
        q.Enqueue(1);
        Assert.That(q.Remove(2), Is.False);
        Assert.That(q.Count, Is.EqualTo(1));
    }

    [Test]
    public void Remove_DuplicateValue_RemovesOneOccurrence()
    {
        var q = new ConcurrentRemovableQueue<int>();
        q.Enqueue(7);
        q.Enqueue(7);
        q.Enqueue(8);

        Assert.That(q.Remove(7), Is.True);
        Assert.That(q.Count, Is.EqualTo(2));

        // One more 7 remains
        var got = new List<int>();
        foreach (var x in q) got.Add(x);
        Assert.That(got.Count(v => v == 7), Is.EqualTo(1));
        Assert.That(got.Count(v => v == 8), Is.EqualTo(1));
    }

    [Test]
    public void Count_MatchesAfterMixedOps()
    {
        var q = new ConcurrentRemovableQueue<int>();
        for (int i = 0; i < 50; i++) q.Enqueue(i);
        for (int i = 0; i < 20; i++) Assert.That(q.Remove(i), Is.True);
        for (int i = 0; i < 15; i++) Assert.That(q.TryDequeue(out _), Is.True);

        Assert.That(q.Count, Is.EqualTo(50 - 20 - 15));
    }

    [Test]
    public void Enumerator_IsStruct_NoAllocationsOnCreation()
    {
        var q = new ConcurrentRemovableQueue<int>();
        q.Enqueue(1);
        var e = q.GetEnumerator();
        Assert.That(e.GetType().IsValueType, Is.True);
        // Sanity: can iterate
        Assert.That(e.MoveNext(), Is.True);
        Assert.That(e.Current, Is.EqualTo(1));
    }

    [Test]
    public void Enumerator_SkipsNodes_RemovedBeforeEnumeration()
    {
        var q = new ConcurrentRemovableQueue<int>();
        for (int i = 0; i < 10; i++) q.Enqueue(i);
        for (int i = 0; i < 5; i++) Assert.That(q.Remove(i), Is.True); // remove first half

        var seen = new HashSet<int>();
        foreach (var x in q) seen.Add(x);

        // Should only see 5..9
        Assert.That(seen.SetEquals(Enumerable.Range(5, 5)), Is.True);
    }

    [Test]
    public void Enumeration_WithConcurrentRemovals_Completes_NoDuplicates()
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
                Assert.That(enumerated.TryAdd(x, 0), Is.True, "Duplicate observed during enumeration.");
            }
        });

        Task.WaitAll(remover, enumeratorTask);

        // All enumerated are within original domain
        Assert.That(enumerated.Keys.All(v => v >= 0 && v < n), Is.True);
    }

    [Test]
    public void Enumeration_WithConcurrentDequeues_Completes_NoDuplicates()
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
                Assert.That(enumerated.TryAdd(x, 0), Is.True);
            }
        });

        Task.WaitAll(consumer, enumeratorTask);
        // All enumerated values valid
        Assert.That(enumerated.Keys.All(v => v >= 0 && v < n), Is.True);
    }

    [Test]
    public void Concurrent_ProducersConsumers_NoLostOrDuplicated()
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

        Assert.That(produced, Is.EqualTo(total));
        Assert.That(consumed, Is.EqualTo(total));

        // Verify uniqueness of values (no duplication)
        Assert.That(bag.Count, Is.EqualTo(total));
        Assert.That(bag.Distinct().Count(), Is.EqualTo(total));
        Assert.That(q.Count, Is.EqualTo(0));
    }

    [Test]
    public void Concurrent_Remove_RandomItems_RemainingCountCorrect()
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

        Assert.That(q.Count, Is.EqualTo(initial - successes));
    }
    
    [Test]
    public void Concurrent_Remove_SameValue_AtMostOneSuccessPerOccurrence()
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

        Assert.That(trueCount, Is.EqualTo(3));
        Assert.That(q.Count, Is.EqualTo(0));
        Assert.That(q.Remove(42), Is.False);
    }

    [Test]
    public void Interleaved_EnqueueAndRemove_CountRemainsConsistent()
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

        Assert.That(q.Count, Is.EqualTo(enqs - rms));
        Assert.That(q.Count, Is.GreaterThanOrEqualTo(0));
    }

    [Test]
    public void TryDequeue_SkipsTombstonedNodes()
    {
        var q = new ConcurrentRemovableQueue<int>();
        q.Enqueue(1);
        q.Enqueue(2);
        q.Enqueue(3);

        Assert.That(q.Remove(1), Is.True);
        Assert.That(q.Remove(2), Is.True);

        Assert.That(q.TryDequeue(out var val), Is.True);
        Assert.That(val, Is.EqualTo(3));
        Assert.That(q.TryDequeue(out _), Is.False);
    }
    
    [Test]
    public void RemoveAll_Then_TryDequeue_ReturnsFalse_CountZero()
    {
        var q = new ConcurrentRemovableQueue<int>();
        for (int i = 0; i < 10; i++) q.Enqueue(i);
        for (int i = 0; i < 10; i++) Assert.That(q.Remove(i), Is.True);

        Assert.That(q.TryDequeue(out _), Is.False);
        Assert.That(q.Count, Is.EqualTo(0));
    }

    [Test]
    public void Enumerator_Reset_AllowsReiteration()
    {
        var q = new ConcurrentRemovableQueue<int>();
        q.Enqueue(5);
        q.Enqueue(6);
        var e = q.GetEnumerator();

        Assert.That(e.MoveNext(), Is.True);
        Assert.That(e.Current, Is.EqualTo(5));

        e.Reset();

        var list = new List<int>();
        while (e.MoveNext()) list.Add(e.Current);

        Assert.That(list, Is.EqualTo(new[] { 5, 6 }));
    }
    
    [Test]
    public void Remove_ValueTypes_Works()
    {
        var q = new ConcurrentRemovableQueue<int>();
        q.Enqueue(100);
        Assert.That(q.Remove(100), Is.True);
        Assert.That(q.Remove(100), Is.False);
    }

    [Test]
    public void Remove_ReferenceTypes_Works()
    {
        var q = new ConcurrentRemovableQueue<string>();
        q.Enqueue("a");
        q.Enqueue("b");
        Assert.That(q.Remove("a"), Is.True);
        Assert.That(q.Remove("a"), Is.False);
        Assert.That(q.TryDequeue(out var left), Is.True);
        Assert.That(left, Is.EqualTo("b"));
    }
    
    [Test]
    public void Enumeration_Progresses_WhileHeadIsRemovedConcurrently()
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
        Assert.That(count, Is.GreaterThanOrEqualTo(0));
        Assert.That(q.Count, Is.GreaterThanOrEqualTo(0));
    }

    [Test]
    public void Enumerator_StartedOnEmpty_DoesNotSeeFutureEnqueues()
    {
        var q = new ConcurrentRemovableQueue<int>();
        var e = q.GetEnumerator(); // snapshot when empty
        q.Enqueue(1);
        q.Enqueue(2);

        Assert.That(e.MoveNext(), Is.False); // should not see items appended after creation
    }

    [Test]
    public void Enumeration_DuringTailAppends_IncludesAtLeastInitial_NoExceptions()
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
        Assert.That(enumerated.IsSupersetOf(Enumerable.Range(0, 100)), Is.True);
    }

    [Test]
    public void CustomComparer_CaseInsensitive_Remove_Works()
    {
        var q = new ConcurrentRemovableQueue<string>(StringComparer.OrdinalIgnoreCase);
        q.Enqueue("FOO");

        Assert.That(q.Remove("foo"), Is.True);
        Assert.That(q.Count, Is.EqualTo(0));
        Assert.That(q.TryDequeue(out _), Is.False);
    }

    [Test]
    public void Dequeue_OrderPreserved_AfterRemovingSome()
    {
        var q = new ConcurrentRemovableQueue<int>();
        for (int i = 0; i < 10; i++) q.Enqueue(i);
        // remove evens
        foreach (var i in Enumerable.Range(0, 10).Where(x => x % 2 == 0))
            Assert.That(q.Remove(i), Is.True);

        var list = new List<int>();
        while (q.TryDequeue(out var v)) list.Add(v);
        Assert.That(list, Is.EqualTo(new[] { 1, 3, 5, 7, 9 }));
    }

    [Test]
    public void MultipleEnumerators_SeeConsistentViews_NoInterference()
    {
        var q = new ConcurrentRemovableQueue<int>();
        for (int i = 0; i < 1000; i++) q.Enqueue(i);

        var s1 = new HashSet<int>();
        var s2 = new HashSet<int>();

        foreach (var x in q) s1.Add(x);
        foreach (var x in q) s2.Add(x);

        Assert.That(s1.SetEquals(Enumerable.Range(0, 1000)), Is.True);
        Assert.That(s2.SetEquals(Enumerable.Range(0, 1000)), Is.True);
    }

    [Test]
    public void Enumerator_Reset_AfterMutations_ReflectsCurrentState()
    {
        var q = new ConcurrentRemovableQueue<int>();
        q.Enqueue(0);
        q.Enqueue(1);
        q.Enqueue(2);

        var e = q.GetEnumerator();
        Assert.That(e.MoveNext(), Is.True); // 0
        Assert.That(e.Current, Is.EqualTo(0));

        // Mutate: remove head and add one at tail
        Assert.That(q.Remove(0), Is.True);
        q.Enqueue(3);

        e.Reset();
        var list = new List<int>();
        while (e.MoveNext()) list.Add(e.Current);

        Assert.That(list, Is.EqualTo(new[] { 1, 2, 3 }));
    }

    [Test]
    public void Remove_Tail_Then_Enqueue_Dequeue_Integrity()
    {
        var q = new ConcurrentRemovableQueue<int>();
        q.Enqueue(1);
        q.Enqueue(2);
        q.Enqueue(3);
        Assert.That(q.Remove(3), Is.True);
        q.Enqueue(4);

        Assert.That(q.TryDequeue(out var a), Is.True);
        Assert.That(a, Is.EqualTo(1));
        Assert.That(q.TryDequeue(out var b), Is.True);
        Assert.That(b, Is.EqualTo(2));
        Assert.That(q.TryDequeue(out var c), Is.True);
        Assert.That(c, Is.EqualTo(4));
        Assert.That(q.TryDequeue(out _), Is.False);
    }

    [Test]
    public void Remove_Head_Then_Enqueue_PreservesOrder()
    {
        var q = new ConcurrentRemovableQueue<int>();
        q.Enqueue(1);
        q.Enqueue(2);
        q.Enqueue(3);
        Assert.That(q.Remove(1), Is.True);
        q.Enqueue(4);

        var seen = new List<int>();
        while (q.TryDequeue(out var v)) seen.Add(v);
        Assert.That(seen, Is.EqualTo(new[] { 2, 3, 4 }));
    }

    [Test]
    public void Remove_All_Then_EnqueueNew_Works()
    {
        var q = new ConcurrentRemovableQueue<int>();
        q.Enqueue(1);
        q.Enqueue(2);
        Assert.That(q.Remove(1), Is.True);
        Assert.That(q.Remove(2), Is.True);

        q.Enqueue(3);
        q.Enqueue(4);

        Assert.That(q.TryDequeue(out var a), Is.True);
        Assert.That(a, Is.EqualTo(3));
        Assert.That(q.TryDequeue(out var b), Is.True);
        Assert.That(b, Is.EqualTo(4));
        Assert.That(q.TryDequeue(out _), Is.False);
    }

    [Test]
    public void ManyEnumerators_WithConcurrentRemovals_AllComplete_NoDuplicatesPerEnumerator()
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
                Assert.That(seen.Add(x), Is.True);
                Assert.That(x, Is.InRange(0, n - 1));
            }
            return seen.Count;
        })).ToArray();

        start.Set();
        Task.WaitAll(tasks.Append(remover).ToArray());

        Assert.That(tasks.All(t => t.Result >= 0), Is.True);
    }

    [Test]
    public void DequeueVsRemove_SingleItem_ExactlyOneWins()
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

        Assert.That(removeSuccess ^ dequeueSuccess, Is.True); // exactly one true
        Assert.That(q.Count, Is.EqualTo(0));
    }

    [Test]
    public void TryDequeue_SkipsManyTombstonedAtFront()
    {
        var q = new ConcurrentRemovableQueue<int>();
        for (int i = 0; i < 10; i++) q.Enqueue(i);
        for (int i = 0; i < 9; i++) Assert.That(q.Remove(i), Is.True);

        Assert.That(q.TryDequeue(out var v), Is.True);
        Assert.That(v, Is.EqualTo(9));
        Assert.That(q.TryDequeue(out _), Is.False);
    }

    [Test]
    public void NullValues_NotSupported_ForReferenceTypes()
    {
        var q = new ConcurrentRemovableQueue<string>();
        Assert.Throws<ArgumentNullException>(() => q.Enqueue(null!));
    }

    [Test]
    public void ValueTypeStruct_Remove_WorksWithDefaultEquality()
    {
        var q = new ConcurrentRemovableQueue<Pair>();
        var p = new Pair(1, 2);
        q.Enqueue(p);

        Assert.That(q.Remove(new Pair(1, 2)), Is.True);
        Assert.That(q.Count, Is.EqualTo(0));
    }

    [Test]
    public void Remove_ExhaustiveDuplicates_FalseAfterExhaustion()
    {
        var q = new ConcurrentRemovableQueue<int>();
        q.Enqueue(7);
        q.Enqueue(7);
        q.Enqueue(7);

        Assert.That(q.Remove(7), Is.True);
        Assert.That(q.Remove(7), Is.True);
        Assert.That(q.Remove(7), Is.True);
        Assert.That(q.Remove(7), Is.False);
        Assert.That(q.Count, Is.EqualTo(0));
    }

    [Test]
    public void RandomConcurrentMix_CountEqualsEnumerationCount_NoExceptions()
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

        Assert.That(q.Count, Is.EqualTo(enumerated));
    }

    [Test]
    public void Enumeration_DuringContinuousEnqueues_Completes()
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
        Assert.That(count, Is.GreaterThanOrEqualTo(500)); // at least initial items were seen
    }

    [Test]
    public void Enumerator_Reset_AfterConcurrentRemovals_ReflectsRemaining()
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

        Assert.That(seen, Is.EqualTo(new[] { 5, 6, 7, 8, 9 }));
        remover.Wait();
    }

    [Test]
    public void RemoveOdds_DequeueEvens_TotalProcessedEqualsInitial()
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

        Assert.That(removed + dequeued, Is.EqualTo(n));
        Assert.That(q.Count, Is.EqualTo(0));
    }

    [Test]
    public void MassiveConcurrentRemovals_SameValue_AllOccurrencesAccounted()
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

        Assert.That(removed + dequeued, Is.EqualTo(occurrences));
        Assert.That(q.Count, Is.EqualTo(0));
    }
    
    [Test]
    public void ConcurrentFuzzing_InvariantsHold()
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
                    Assert.That(q.Count, Is.GreaterThanOrEqualTo(0));
                }
            }
        })).ToArray();

        start.Set();
        Task.WaitAll(tasks);

        // --- Postconditions (no concurrent writers now) ---
        // 1) Count equals number of items we can enumerate
        int enumeratedCount = 0;
        foreach (var _ in q) enumeratedCount++;
        Assert.That(q.Count, Is.EqualTo(enumeratedCount), "Count != enumerated size at end.");

        // 2) Conservation: enqueued == removed + dequeued + remaining
        Assert.That(enq, Is.EqualTo(rm + deq + q.Count),
            $"Conservation failed: enq={enq}, rm={rm}, deq={deq}, remaining={q.Count}");

        // 3) A second pass should match the first (enumerator stability once quiescent)
        int secondPass = 0;
        foreach (var _ in q) secondPass++;
        Assert.That(secondPass, Is.EqualTo(enumeratedCount));
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
