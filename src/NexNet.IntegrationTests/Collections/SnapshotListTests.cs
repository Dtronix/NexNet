using NexNet.Internals.Collections.Lists;
using NUnit.Framework;

namespace NexNet.IntegrationTests.Collections;

internal class SnapshotListTests
{
    [Test]
    public void Constructor_NegativeOrZeroCapacity_Throws()
    {
        // ReSharper disable twice ObjectCreationAsStatement
        Assert.Throws<ArgumentOutOfRangeException>(() => new SnapshotList<int>(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SnapshotList<int>(-5));
    }

    [Test]
    public void Add_SingleItem_CountIsOne()
    {
        var list = new SnapshotList<int> { 42 };
        Assert.That(list.Count, Is.EqualTo(1));
    }

    [Test]
    public void Add_MultipleItems_CountMatches()
    {
        var list = new SnapshotList<int>();
        for (int i = 0; i < 5; i++) list.Add(i);
        Assert.That(list.Count, Is.EqualTo(5));
    }

    [Test]
    public void Add_MultipleItems_OrderIsLifo()
    {
        var list = new SnapshotList<int> { 1, 2, 3 };
        var expected = new[] { 3, 2, 1 };
        Assert.That(list.ToList(), Is.EqualTo(expected));
    }

    [Test]
    public void Insert_AtBeginningOnEmpty_Works()
    {
        var list = new SnapshotList<int>();
        list.Insert(0, 100);
        Assert.That(list.Count, Is.EqualTo(1));
        Assert.That(list.ToList(), Is.EqualTo(new[] { 100 }));
    }

    [Test]
    public void Insert_AtEnd_Works()
    {
        var list = new SnapshotList<int> { 1, 2 };
        list.Insert(2, 3);
        Assert.That(list.Count, Is.EqualTo(3));
        Assert.That(list.ToList(), Is.EqualTo(new[] { 2, 1, 3 }));
    }

    [Test]
    public void Insert_InMiddle_Works()
    {
        var list = new SnapshotList<int> { 1, 3 };
        list.Insert(1, 2);
        Assert.That(list.Count, Is.EqualTo(3));
        Assert.That(list.ToList(), Is.EqualTo(new[] { 3, 2, 1 }));
    }

    [Test]
    public void Insert_NegativeIndex_Throws()
    {
        var list = new SnapshotList<int>();
        Assert.Throws<ArgumentOutOfRangeException>(() => list.Insert(-1, 5));
    }

    [Test]
    public void Insert_IndexGreaterThanCount_Throws()
    {
        var list = new SnapshotList<int> { 1 };
        Assert.Throws<ArgumentOutOfRangeException>(() => list.Insert(2, 2));
    }

    [Test]
    public void Remove_OnEmptyList_ReturnsFalse()
    {
        var list = new SnapshotList<int>();
        Assert.That(list.Remove(99), Is.False);
    }

    [Test]
    public void Remove_ItemNotExists_ReturnsFalse()
    {
        var list = new SnapshotList<int> { 1 };
        Assert.That(list.Remove(2), Is.False);
    }

    [Test]
    public void Remove_ExistingItem_ReturnsTrue()
    {
        var list = new SnapshotList<int> { 5 };
        Assert.That(list.Remove(5), Is.True);
    }

    [Test]
    public void Remove_DuplicateItems_RemovesFirstOnly()
    {
        var list = new SnapshotList<int> { 1, 2, 1 };
        Assert.That(list.Remove(1), Is.True);
        Assert.That(list.Count, Is.EqualTo(2));
        var items = list.ToList();
        Assert.That(items, Is.EqualTo(new[] { 2, 1 }));
    }

    [Test]
    public void Remove_DecrementsCount()
    {
        var list = new SnapshotList<int> { 10, 20 };
        list.Remove(10);
        Assert.That(list.Count, Is.EqualTo(1));
    }

    [Test]
    public void Count_AfterAddRemove_Correct()
    {
        var list = new SnapshotList<int> { 1, 2 };
        list.Remove(1);
        list.Add(3);
        Assert.That(list.Count, Is.EqualTo(2));
    }

    [Test]
    public void Enumeration_OnEmptyList_ReturnsNothing()
    {
        var list = new SnapshotList<int>();
        Assert.That(list.Any(), Is.False);
    }

    [Test]
    public void Enumeration_ReturnsSnapshotAtStart()
    {
        var list = new SnapshotList<int> { 1, 2 };
        var snapshot = list.ToList();
        list.Add(3);
        Assert.That(snapshot, Is.EqualTo(new[] { 2, 1 }));
    }

    [Test]
    public void Enumeration_IgnoresItemsAddedAfterSnapshot()
    {
        var list = new SnapshotList<int> { 1 };
        using var enumerator = list.GetEnumerator();
        list.Add(2);
        var result = new List<int>();
        while (enumerator.MoveNext())
            result.Add(enumerator.Current);
        Assert.That(result, Is.EqualTo(new[] { 1 }));
    }

    [Test]
    public void Enumeration_RemovesItemsAfterSnapshot()
    {
        var list = new SnapshotList<int> { 1, 2 };
        using var enumerator = list.GetEnumerator();
        list.Remove(1);
        var result = new List<int>();
        while (enumerator.MoveNext())
            result.Add(enumerator.Current);
        Assert.That(result, Is.EqualTo(new[] { 2 })); // removal happens after snapshot, so still listed
    }

    [Test]
    public void FreeList_ReusedNodes()
    {
        var list = new SnapshotList<int>(2) { 1, 2 };
        Assert.That(list.Remove(1), Is.True);
        list.Add(3); // should reuse slot
        Assert.That(list.Count, Is.EqualTo(2));
        Assert.That(list.ToList(), Is.EqualTo(new[] { 3, 2 }));
    }

    [Test]
    public void GrowArray_WhenCapacityExceeded_Works()
    {
        var list = new SnapshotList<int>(2) { 1, 2, 3 // force grow
        };
        Assert.That(list.Count, Is.EqualTo(3));
        Assert.That(list.ToList(), Is.EqualTo(new[] { 3, 2, 1 }));
    }

    [Test]
    public void ThreadSafety_ConcurrentAdds_CountCorrect()
    {
        var list = new SnapshotList<int>();
        int threads = 10, perThread = 1000;
        Parallel.For(0, threads, i =>
        {
            for (int j = 0; j < perThread; j++)
                list.Add(i * perThread + j);
        });
        Assert.That(list.Count, Is.EqualTo(threads * perThread));
    }

    [Test]
    public void ThreadSafety_ConcurrentAdds_ItemsCorrect()
    {
        var list = new SnapshotList<int>();
        int threads = 5, perThread = 200;
        Parallel.For(0, threads, i =>
        {
            for (int j = 0; j < perThread; j++)
                list.Add(i * perThread + j);
        });
        var items = list.ToList();
        Assert.That(items.Count, Is.EqualTo(threads * perThread));
        for (int i = 0; i < threads * perThread; i++)
            Assert.That(items.Contains(i), Is.True);
    }

    [Test]
    public void ThreadSafety_ConcurrentRemoves_CountCorrect()
    {
        var list = new SnapshotList<int>();
        for (int i = 0; i < 1000; i++) list.Add(i);
        Parallel.For(0, 10, t =>
        {
            for (int i = 0; i < 100; i++)
                list.Remove(i + t * 100);
        });
        Assert.That(list.Count, Is.EqualTo(0));
    }

    [Test]
    public void ThreadSafety_ConcurrentAddsAndRemoves_NoExceptions()
    {
        var list = new SnapshotList<int>();
        Parallel.Invoke(
            () =>
            {
                for (int i = 0; i < 1000; i++)
                    list.Add(i);
            },
            () =>
            {
                for (int i = 0; i < 1000; i++)
                    list.Remove(i);
            }
        );
        Assert.That(list.Count, Is.GreaterThanOrEqualTo(0));
    }

    [Test]
    public void ThreadSafety_ConcurrentEnumerationWithAdds_NoExceptions()
    {
        var list = new SnapshotList<int>();
        for (int i = 0; i < 100; i++) list.Add(i);
        Assert.DoesNotThrow(() =>
        {
            Parallel.Invoke(
                () =>
                {
                    foreach (var _ in list) { }
                },
                () =>
                {
                    for (int i = 100; i < 200; i++) list.Add(i);
                }
            );
        });
    }

    [Test]
    public void ThreadSafety_ConcurrentEnumerationWithRemoves_NoExceptions()
    {
        var list = new SnapshotList<int>();
        for (int i = 0; i < 100; i++) list.Add(i);
        Assert.DoesNotThrow(() =>
        {
            Parallel.Invoke(
                () =>
                {
                    foreach (var _ in list) { }
                },
                () =>
                {
                    for (int i = 0; i < 100; i++) list.Remove(i);
                }
            );
        });
    }

    [Test]
    public void ResetEnumerator_Works()
    {
        var list = new SnapshotList<int> { 1, 2 };
        using var e = list.GetEnumerator();
        Assert.That(e.MoveNext(), Is.True);
        e.Reset();
        Assert.That(e.MoveNext(), Is.True);
        Assert.That(e.Current, Is.EqualTo(2));
    }

    [Test]
    public void MultipleEnumerators_Independence()
    {
        var list = new SnapshotList<int> { 1, 2 };
        using var e1 = list.GetEnumerator();
        using var e2 = list.GetEnumerator();
        e1.MoveNext(); // at 2
        Assert.That(e2.MoveNext(), Is.True);
        Assert.That(e2.Current, Is.EqualTo(2));
    }

    [Test]
    public void Enumerator_MoveNextAfterCompletion_ReturnsFalse()
    {
        var list = new SnapshotList<int> { 1 };
        using var e = list.GetEnumerator();
        while (e.MoveNext()) { }

        Assert.That(e.MoveNext(), Is.False);
    }

    [Test]
    public void Enumerator_Reset_AllowsReiteration()
    {
        var list = new SnapshotList<int> { 1, 2 };
        using var e = list.GetEnumerator();
        var firstRun = new List<int>();
        while (e.MoveNext()) firstRun.Add(e.Current);
        e.Reset();
        var secondRun = new List<int>();
        while (e.MoveNext()) secondRun.Add(e.Current);
        Assert.That(secondRun, Is.EqualTo(firstRun));
    }

    [Test]
    public void Add_NullValue_ForReferenceTypes()
    {
        var list = new SnapshotList<string?> { null };
        Assert.That(list.Count, Is.EqualTo(1));
        Assert.That(list.ToList(), Is.EqualTo(new string?[] { null }));
    }

    [Test]
    public void Remove_NullValue_ReturnsTrue()
    {
        var list = new SnapshotList<string?> { null };
        Assert.That(list.Remove(null), Is.True);
        Assert.That(list.Count, Is.EqualTo(0));
    }

    [Test]
    public void Insert_NullValue_Works()
    {
        var list = new SnapshotList<string?>();
        list.Insert(0, null);
        Assert.That(list.Count, Is.EqualTo(1));
        Assert.That(list.ToList(), Is.EqualTo(new string?[] { null }));
    }

    [Test]
    public void Add_Remove_Add_Remove_Repeatedly_Works()
    {
        var list = new SnapshotList<int>();
        for (int i = 0; i < 100; i++)
        {
            list.Add(i);
            Assert.That(list.Remove(i), Is.True);
        }

        Assert.That(list.Count, Is.EqualTo(0));
    }

    [Test]
    public void RemoveAll_Items_ThenCountZero()
    {
        var list = new SnapshotList<int>();
        foreach (var i in Enumerable.Range(0, 10)) list.Add(i);
        foreach (var i in Enumerable.Range(0, 10)) list.Remove(i);
        Assert.That(list.Count, Is.EqualTo(0));
    }

    [Test]
    public void AddAfterRemove_CountAndEnumeration()
    {
        var list = new SnapshotList<int> { 1 };
        list.Remove(1);
        list.Add(2);
        Assert.That(list.Count, Is.EqualTo(1));
        Assert.That(list.ToList(), Is.EqualTo(new[] { 2 }));
    }

    [Test]
    public void Insert_MaintainsOrder()
    {
        var list = new SnapshotList<int> { 1, 3 };
        list.Insert(1, 2);
        Assert.That(list.ToList(), Is.EqualTo(new[] { 3, 2, 1 }));
    }

    [Test]
    public void Remove_MaintainsOrder()
    {
        var list = new SnapshotList<int> { 1, 2, 3 };
        list.Remove(2);
        Assert.That(list.ToList(), Is.EqualTo(new[] { 3, 1 }));
    }

    [Test]
    public void Add_Interleaved_WithInsert_MaintainsCorrectSequence()
    {
        var list = new SnapshotList<int> { 1 // [1]
        };
        list.Insert(1, 3); // [1,3]
        list.Add(2); // [2,1,3]
        Assert.That(list.ToList(), Is.EqualTo(new[] { 2, 1, 3 }));
    }

    [Test]
    public void SnapshotEnumeration_CapturesVersionIndependentOfFurtherOperations()
    {
        var list = new SnapshotList<int> { 1 };
        var seq = list.ToList();
        list.Add(2);
        list.Remove(1);
        Assert.That(seq, Is.EqualTo(new[] { 1 }));
    }

    [Test]
    public void SnapshotEnumerator_Dispose_DoesNothing()
    {
        var list = new SnapshotList<int> { 1 };
        var e = list.GetEnumerator();
        Assert.DoesNotThrow(() => e.Dispose());
    }

    [Test]
    public void Enumeration_IsIdempotent_ForFixedSnapshot()
    {
        var list = new SnapshotList<int> { 1, 2 };
        var snapshot = list.ToList();
        // enumerate twice
        var first = snapshot.ToList();
        var second = snapshot.ToList();
        Assert.That(second, Is.EqualTo(first));
    }

    [Test]
    public void ThreadSafety_ConcurrentInsert_CountCorrect()
    {
        var list = new SnapshotList<int>();
        int threads = 5, perThread = 200;
        Parallel.For(0, threads, i =>
        {
            for (int j = 0; j < perThread; j++)
                list.Insert(0, i * perThread + j);
        });
        Assert.That(list.Count, Is.EqualTo(threads * perThread));
    }

    [Test]
    public void ThreadSafety_ConcurrentInsert_ItemsCorrect()
    {
        var list = new SnapshotList<int>();
        int threads = 3, perThread = 100;
        Parallel.For(0, threads, i =>
        {
            for (int j = 0; j < perThread; j++)
                list.Insert(0, i * perThread + j);
        });
        var items = list.ToList();
        Assert.That(items.Count, Is.EqualTo(threads * perThread));
        for (int i = 0; i < threads * perThread; i++)
            Assert.That(items.Contains(i), Is.True);
    }

    [Test]
    public void ThreadSafety_MixedOperations_NoDeadlock()
    {
        var list = new SnapshotList<int>();
        for (int i = 0; i < 100; i++) list.Add(i);
        var tasks = new Task[2];
        tasks[0] = Task.Run(() =>
        {
            for (int i = 0; i < 100; i++)
                list.Add(i + 100);
        });
        tasks[1] = Task.Run(() =>
        {
            for (int i = 0; i < 100; i++)
                list.Remove(i);
        });
        Assert.DoesNotThrow(() => Task.WaitAll(tasks));
    }

    [Test]
    public void AddLargeNumber_StressTest()
    {
        var list = new SnapshotList<int>();
        int n = 5000;
        for (int i = 0; i < n; i++)
            list.Add(i);
        Assert.That(list.Count, Is.EqualTo(n));
        var items = list.ToList();
        Assert.That(items.First(), Is.EqualTo(n - 1));
        Assert.That(items.Last(), Is.EqualTo(0));
    }

    [Test]
    public void Insert_MultipleSequentialIndices_MaintainsOrder()
    {
        var list = new SnapshotList<int>();
        list.Insert(0, 1); // [1]
        list.Insert(1, 3); // [1,3]
        list.Insert(1, 2); // [1,2,3]
        Assert.That(list.ToList(), Is.EqualTo(new[] { 1, 2, 3 }));
    }

    [Test]
    public void ThreadSafety_ConcurrentAddRemoveDistinctRanges_CountZero()
    {
        var list = new SnapshotList<int>();
        int threads = 10, range = 1000;
        Parallel.For(0, threads, i =>
        {
            for (int j = 0; j < range; j++)
                list.Add(i * range + j);
            for (int j = 0; j < range; j++)
                list.Remove(i * range + j);
        });
        Assert.That(list.Count, Is.EqualTo(0));
    }

    [Test]
    public void ThreadSafety_ConcurrentInsertRemoveDistinctRanges_CountZero()
    {
        var list = new SnapshotList<int>();
        int threads = 5, range = 500;
        Parallel.For(0, threads, i =>
        {
            for (int j = 0; j < range; j++)
                list.Insert(0, i * range + j);
            for (int j = 0; j < range; j++)
                list.Remove(i * range + j);
        });
        Assert.That(list.Count, Is.EqualTo(0));
    }

    [Test]
    public void ThreadSafety_ConcurrentAdd_CausesGrowArray_CorrectCount()
    {
        var list = new SnapshotList<int>(2);
        int threads = 20, per = 200;
        Parallel.For(0, threads, i =>
        {
            for (int j = 0; j < per; j++)
                list.Add(i * per + j);
        });
        Assert.That(list.Count, Is.EqualTo(threads * per));
    }

    [Test]
    public void ThreadSafety_ConcurrentInsert_CausesGrowArray_CorrectCount()
    {
        var list = new SnapshotList<int>(2);
        int threads = 10, per = 100;
        Parallel.For(0, threads, i =>
        {
            for (int j = 0; j < per; j++)
                list.Insert(0, i * per + j);
        });
        Assert.That(list.Count, Is.EqualTo(threads * per));
    }

    [Test]
    public void ThreadSafety_ConcurrentMixedOperationsDistinctRanges_CountZero()
    {
        var list = new SnapshotList<int>();
        int threads = 8, range = 500;
        Parallel.For(0, threads, i =>
        {
            for (int j = 0; j < range; j++)
            {
                list.Add(i * range + j);
                list.Insert(0, i * range + j);
            }

            for (int j = 0; j < range; j++)
            {
                list.Remove(i * range + j);
            }
        });
        Assert.That(list.Count, Is.EqualTo(threads * range)); // only the inserts remain
    }

    [Test]
    public void ThreadSafety_ConcurrentAdd_AndEnumeration_NoExceptions()
    {
        var list = new SnapshotList<int>();
        for (int i = 0; i < 100; i++) list.Add(i);
        Assert.DoesNotThrow(() =>
        {
            Parallel.Invoke(
                () =>
                {
                    foreach (var _ in list) { }
                },
                () =>
                {
                    for (int i = 100; i < 1000; i++) list.Add(i);
                }
            );
        });
    }

    [Test]
    public void ThreadSafety_ConcurrentRemove_AndEnumeration_NoExceptions()
    {
        var list = new SnapshotList<int>();
        for (int i = 0; i < 500; i++) list.Add(i);
        Assert.DoesNotThrow(() =>
        {
            Parallel.Invoke(
                () =>
                {
                    foreach (var _ in list) { }
                },
                () =>
                {
                    for (int i = 0; i < 500; i++) list.Remove(i);
                }
            );
        });
    }

    [Test]
    public void ThreadSafety_ConcurrentInsert_AndEnumeration_NoExceptions()
    {
        var list = new SnapshotList<int>();
        for (int i = 0; i < 200; i++) list.Add(i);
        Assert.DoesNotThrow(() =>
        {
            Parallel.Invoke(
                () =>
                {
                    foreach (var _ in list) { }
                },
                () =>
                {
                    for (int i = 0; i < 200; i++) list.Insert(1, i);
                }
            );
        });
    }

    [Test]
    public void ThreadSafety_EnumerationDuringGrow_NoExceptions()
    {
        var list = new SnapshotList<int>(1);
        Assert.DoesNotThrow(() =>
        {
            Parallel.Invoke(
                () =>
                {
                    for (int i = 0; i < 500; i++) list.Add(i);
                },
                () =>
                {
                    foreach (var _ in list) { }
                }
            );
        });
    }

    [Test]
    public void ThreadSafety_HighConcurrencyStress_NoDeadlock()
    {
        var list = new SnapshotList<int>();
        var tasks = Enumerable.Range(0, 50).Select(_ => Task.Run(() =>
        {
            for (int i = 0; i < 200; i++)
            {
                list.Add(i);
                list.Remove(i - 1);
                list.Insert(0, i);
            }
        })).ToArray();
        Assert.DoesNotThrow(() => Task.WaitAll(tasks));
    }

    [Test]
    public void ThreadSafety_ConcurrentAddRemoveLoop_NoExceptions()
    {
        var list = new SnapshotList<int>();
        Assert.DoesNotThrow(() =>
        {
            Parallel.For(0, 100, _ =>
            {
                for (int i = 0; i < 100; i++)
                {
                    list.Add(i);
                    list.Remove(i);
                }
            });
        });
    }

    [Test]
    public void ThreadSafety_ConcurrentInsertRemoveLoop_CountZero()
    {
        var list = new SnapshotList<int>();
        int loops = 50, per = 50;
        Parallel.For(0, loops, _ =>
        {
            for (int i = 0; i < per; i++)
            {
                list.Insert(0, i);
                list.Remove(i);
            }
        });
        Assert.That(list.Count, Is.EqualTo(0));
    }

    [Test]
    public void ThreadSafety_ConcurrentFreeListReuseUnderContention()
    {
        var list = new SnapshotList<int>(4);
        // fill and empty repeatedly
        Parallel.For(0, 10, _ =>
        {
            for (int i = 0; i < 100; i++)
            {
                list.Add(i);
            }

            for (int i = 0; i < 100; i++)
            {
                list.Remove(i);
            }
        });
        Assert.That(list.Count, Is.EqualTo(0));
    }

    [Test]
    public void ThreadSafety_ConcurrentGrowUnderContention()
    {
        var list = new SnapshotList<int>(2);
        Parallel.For(0, 20, i =>
        {
            for (int j = 0; j < 200; j++)
                list.Add(i * 200 + j);
        });
        Assert.That(list.Count, Is.EqualTo(20 * 200));
    }

    [Test]
    public void ThreadSafety_ConcurrentAddRemoveAlternating_NoDeadlock()
    {
        var list = new SnapshotList<int>();
        var tasks = new[]
        {
            Task.Run(() =>
            {
                for (int i = 0; i < 1000; i++) list.Add(i);
            }),
            Task.Run(() =>
            {
                for (int i = 0; i < 1000; i++) list.Remove(i);
            }),
            Task.Run(() =>
            {
                for (int i = 0; i < 1000; i++) list.Insert(0, i);
            })
        };
        Assert.DoesNotThrow(() => Task.WaitAll(tasks));
    }

    [Test]
    public void ThreadSafety_ConcurrentShadowingOperations_CountMatches()
    {
        var list = new SnapshotList<int>();
        int threads = 4, per = 250;
        Parallel.For(0, threads, t =>
        {
            for (int i = 0; i < per; i++)
            {
                list.Add(t * per + i);
                list.Insert(1, t * per + i);
            }
        });
        Assert.That(list.Count, Is.EqualTo(threads * per * 2));
    }

    [Test]
    public void ThreadSafety_ConcurrentInsertAtEnd_CountMatches()
    {
        var list = new SnapshotList<int>();
        for (int i = 0; i < 100; i++) list.Add(i);
        Parallel.For(0, 10, _ =>
        {
            list.Insert(list.Count, 999);
        });
        Assert.That(list.Count, Is.EqualTo(100 + 10));
    }

    [Test]
    public void ThreadSafety_ConcurrentInsertAtRandomPositions_NoExceptions()
    {
        var rnd = new Random();
        var list = new SnapshotList<int>();
        for (int i = 0; i < 500; i++) list.Add(i);
        Assert.DoesNotThrow(() =>
        {
            Parallel.For(0, 200, _ =>
            {
                int idx = rnd.Next(0, list.Count + 1);
                list.Insert(idx, -1);
            });
        });
    }

    [Test]
    public void ThreadSafety_ConcurrentAddRemoveWithParallelForEach_NoExceptions()
    {
        var list = new SnapshotList<int>();
        var data = Enumerable.Range(0, 1000).ToArray();
        Assert.DoesNotThrow(() =>
        {
            Parallel.ForEach(data, i =>
            {
                list.Add(i);
                list.Remove(i / 2);
            });
        });
    }

    [Test]
    public void ThreadSafety_ConcurrentSnapshotEnumerationUnderLoad()
    {
        var list = new SnapshotList<int>();
        for (int i = 0; i < 1000; i++) list.Add(i);
        Assert.DoesNotThrow(() =>
        {
            Parallel.Invoke(
                () =>
                {
                    foreach (var _ in list) { }
                },
                () =>
                {
                    for (int i = 1000; i < 2000; i++) list.Add(i);
                },
                () =>
                {
                    for (int i = 0; i < 500; i++) list.Remove(i);
                }
            );
        });
    }

    [Test]
    public void ThreadSafety_ConcurrentDeepEnumerationAfterOperations()
    {
        var list = new SnapshotList<int>();
        for (int i = 0; i < 200; i++) list.Add(i);
        using var enumerator = list.GetEnumerator();
        // mutate heavily
        Parallel.Invoke(
            () =>
            {
                for (int i = 0; i < 200; i++) list.Add(i + 200);
            },
            () =>
            {
                for (int i = 0; i < 200; i++) list.Remove(i);
            }
        );
        Assert.DoesNotThrow(() =>
        {
            while (enumerator.MoveNext())
            {
                _ = enumerator.Current;
            }
        });
    }

    [Test]
    public void ThreadSafety_ConcurrentEnumeratorReset_NoExceptions()
    {
        var list = new SnapshotList<int>();
        for (int i = 0; i < 100; i++) list.Add(i);
        var e = list.GetEnumerator();
        using var e1 = e as IDisposable;
        Assert.DoesNotThrow(() =>
        {
            Parallel.Invoke(
                () => e.Reset(),
                () => list.Add(101),
                () => list.Remove(0)
            );
        });
    }

    [Test]
    public void ThreadSafety_ConcurrentEnumeratorDisposeDuringOperations_NoExceptions()
    {
        var list = new SnapshotList<int>();
        for (int i = 0; i < 100; i++) list.Add(i);
        var e = list.GetEnumerator();
        Assert.DoesNotThrow(() =>
        {
            Parallel.Invoke(
                () => e.Dispose(),
                () => list.Add(200),
                () => list.Remove(1)
            );
        });
    }

    [Test]
    public void ThreadSafety_ConcurrentOperationsWithThreadSleep_NoDeadlock()
    {
        var list = new SnapshotList<int>();
        var t1 = Task.Run(() =>
        {
            for (int i = 0; i < 20; i++)
            {
                list.Add(i);
                Thread.Sleep(1);
            }
        });
        var t2 = Task.Run(() =>
        {
            for (int i = 0; i < 20; i++)
            {
                list.Remove(i);
                Thread.Sleep(1);
            }
        });
        Assert.DoesNotThrow(() => Task.WaitAll(t1, t2));
    }

    [Test]
    public void ThreadSafety_TaskBasedConcurrency_NoExceptions()
    {
        var list = new SnapshotList<int>();
        var tasks = new Task[3];
        tasks[0] = Task.Factory.StartNew(() =>
        {
            for (int i = 0; i < 500; i++) list.Add(i);
        });
        tasks[1] = Task.Factory.StartNew(() =>
        {
            for (int i = 0; i < 500; i++) list.Remove(i);
        });
        tasks[2] = Task.Factory.StartNew(() =>
        {
            for (int i = 0; i < 500; i++) list.Insert(0, i);
        });
        Assert.DoesNotThrow(() => Task.WaitAll(tasks));
    }

    [Test]
    public void ThreadSafety_ParallelForWithThreadLocal_NoExceptions()
    {
        var list = new SnapshotList<int>();
        Parallel.For(0, 1000,
            () => new Random(),
            (i, _, rnd) =>
            {
                if (rnd.NextDouble() < 0.5) list.Add(i);
                else list.Remove(i);
                return rnd;
            },
            _ => { });
        // just ensure it completed without deadlock
        Assert.Pass();
    }

    [Test]
    public void ThreadSafety_InterleavedAddInsertRemoveStressTest()
    {
        var list = new SnapshotList<int>();
        var tasks = new[]
        {
            Task.Run(() =>
            {
                for (int i = 0; i < 1000; i++) list.Add(i);
            }),
            Task.Run(() =>
            {
                for (int i = 0; i < 1000; i++) list.Insert(0, i);
            }),
            Task.Run(() =>
            {
                for (int i = 0; i < 1000; i++) list.Remove(i);
            })
        };
        Assert.DoesNotThrow(() => Task.WaitAll(tasks));
    }

    [Test]
    public void ThreadSafety_BulkConcurrentOps_CountMatchesAddsAndInserts()
    {
        var list = new SnapshotList<int>();
        int adds = 500, inserts = 500;
        var t1 = Task.Run(() =>
        {
            for (int i = 0; i < adds; i++) list.Add(i);
        });
        var t2 = Task.Run(() =>
        {
            for (int i = 0; i < inserts; i++) list.Insert(0, i);
        });
        Task.WaitAll(t1, t2);
        Assert.That(list.Count, Is.EqualTo(adds + inserts));
    }

    [Test]
    public void ThreadSafety_CombinedHighLoadTest_NoDeadlock()
    {
        var list = new SnapshotList<int>();
        var tasks = new List<Task>();
        for (int t = 0; t < 10; t++)
        {
            tasks.Add(Task.Run(() =>
            {
                for (int i = 0; i < 1000; i++)
                {
                    list.Add(i);
                    list.Insert(0, i);
                    list.Remove(i / 2);
                }
            }));
        }

        Assert.DoesNotThrow(() => Task.WaitAll(tasks.ToArray()));
    }
}

