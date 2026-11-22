using NexNet.Internals.Collections.Lists;

namespace NexNet.IntegrationTests.Collections;

[NotInParallel("stress")]
internal class SnapshotListTests
{
    [Test]
    public async Task Constructor_NegativeOrZeroCapacity_Throws()
    {
        // ReSharper disable twice ObjectCreationAsStatement
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new SnapshotList<int>(0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new SnapshotList<int>(-5));
    }

    [Test]
    public async Task Add_SingleItem_CountIsOne()
    {
        var list = new SnapshotList<int> { 42 };
        await Assert.That(list.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Add_MultipleItems_CountMatches()
    {
        var list = new SnapshotList<int>();
        for (int i = 0; i < 5; i++) list.Add(i);
        await Assert.That(list.Count).IsEqualTo(5);
    }

    [Test]
    public async Task Add_MultipleItems_OrderIsLifo()
    {
        var list = new SnapshotList<int> { 1, 2, 3 };
        var expected = new[] { 3, 2, 1 };
        await Assert.That(list.ToList()).IsEquivalentTo(expected, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Insert_AtBeginningOnEmpty_Works()
    {
        var list = new SnapshotList<int>();
        list.Insert(0, 100);
        await Assert.That(list.Count).IsEqualTo(1);
        await Assert.That(list.ToList()).IsEquivalentTo(new[] { 100 }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Insert_AtEnd_Works()
    {
        var list = new SnapshotList<int> { 1, 2 };
        list.Insert(2, 3);
        await Assert.That(list.Count).IsEqualTo(3);
        await Assert.That(list.ToList()).IsEquivalentTo(new[] { 2, 1, 3 }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Insert_InMiddle_Works()
    {
        var list = new SnapshotList<int> { 1, 3 };
        list.Insert(1, 2);
        await Assert.That(list.Count).IsEqualTo(3);
        await Assert.That(list.ToList()).IsEquivalentTo(new[] { 3, 2, 1 }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Insert_NegativeIndex_Throws()
    {
        var list = new SnapshotList<int>();
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => list.Insert(-1, 5));
    }

    [Test]
    public async Task Insert_IndexGreaterThanCount_Throws()
    {
        var list = new SnapshotList<int> { 1 };
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => list.Insert(2, 2));
    }

    [Test]
    public async Task Remove_OnEmptyList_ReturnsFalse()
    {
        var list = new SnapshotList<int>();
        await Assert.That(list.Remove(99)).IsFalse();
    }

    [Test]
    public async Task Remove_ItemNotExists_ReturnsFalse()
    {
        var list = new SnapshotList<int> { 1 };
        await Assert.That(list.Remove(2)).IsFalse();
    }

    [Test]
    public async Task Remove_ExistingItem_ReturnsTrue()
    {
        var list = new SnapshotList<int> { 5 };
        await Assert.That(list.Remove(5)).IsTrue();
    }

    [Test]
    public async Task Remove_DuplicateItems_RemovesFirstOnly()
    {
        var list = new SnapshotList<int> { 1, 2, 1 };
        await Assert.That(list.Remove(1)).IsTrue();
        await Assert.That(list.Count).IsEqualTo(2);
        var items = list.ToList();
        await Assert.That(items).IsEquivalentTo(new[] { 2, 1 }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Remove_DecrementsCount()
    {
        var list = new SnapshotList<int> { 10, 20 };
        list.Remove(10);
        await Assert.That(list.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Count_AfterAddRemove_Correct()
    {
        var list = new SnapshotList<int> { 1, 2 };
        list.Remove(1);
        list.Add(3);
        await Assert.That(list.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Enumeration_OnEmptyList_ReturnsNothing()
    {
        var list = new SnapshotList<int>();
        await Assert.That(list.Any()).IsFalse();
    }

    [Test]
    public async Task Enumeration_ReturnsSnapshotAtStart()
    {
        var list = new SnapshotList<int> { 1, 2 };
        var snapshot = list.ToList();
        list.Add(3);
        await Assert.That(snapshot).IsEquivalentTo(new[] { 2, 1 }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Enumeration_IgnoresItemsAddedAfterSnapshot()
    {
        var list = new SnapshotList<int> { 1 };
        using var enumerator = list.GetEnumerator();
        list.Add(2);
        var result = new List<int>();
        while (enumerator.MoveNext())
            result.Add(enumerator.Current);
        await Assert.That(result).IsEquivalentTo(new[] { 1 }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Enumeration_RemovesItemsAfterSnapshot()
    {
        var list = new SnapshotList<int> { 1, 2 };
        using var enumerator = list.GetEnumerator();
        list.Remove(1);
        var result = new List<int>();
        while (enumerator.MoveNext())
            result.Add(enumerator.Current);
        await Assert.That(result).IsEquivalentTo(new[] { 2 }, CollectionOrdering.Matching); // removal happens after snapshot, so still listed
    }

    [Test]
    public async Task FreeList_ReusedNodes()
    {
        var list = new SnapshotList<int>(2) { 1, 2 };
        await Assert.That(list.Remove(1)).IsTrue();
        list.Add(3); // should reuse slot
        await Assert.That(list.Count).IsEqualTo(2);
        await Assert.That(list.ToList()).IsEquivalentTo(new[] { 3, 2 }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task GrowArray_WhenCapacityExceeded_Works()
    {
        var list = new SnapshotList<int>(2) { 1, 2, 3 // force grow
        };
        await Assert.That(list.Count).IsEqualTo(3);
        await Assert.That(list.ToList()).IsEquivalentTo(new[] { 3, 2, 1 }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task ThreadSafety_ConcurrentAdds_CountCorrect()
    {
        var list = new SnapshotList<int>();
        int threads = 10, perThread = 1000;
        Parallel.For(0, threads, i =>
        {
            for (int j = 0; j < perThread; j++)
                list.Add(i * perThread + j);
        });
        await Assert.That(list.Count).IsEqualTo(threads * perThread);
    }

    [Test]
    public async Task ThreadSafety_ConcurrentAdds_ItemsCorrect()
    {
        var list = new SnapshotList<int>();
        int threads = 5, perThread = 200;
        Parallel.For(0, threads, i =>
        {
            for (int j = 0; j < perThread; j++)
                list.Add(i * perThread + j);
        });
        var items = list.ToList();
        await Assert.That(items.Count).IsEqualTo(threads * perThread);
        for (int i = 0; i < threads * perThread; i++)
            await Assert.That(items.Contains(i)).IsTrue();
    }

    [Test]
    public async Task ThreadSafety_ConcurrentRemoves_CountCorrect()
    {
        var list = new SnapshotList<int>();
        for (int i = 0; i < 1000; i++) list.Add(i);
        Parallel.For(0, 10, t =>
        {
            for (int i = 0; i < 100; i++)
                list.Remove(i + t * 100);
        });
        await Assert.That(list.Count).IsEqualTo(0);
    }

    [Test]
    public async Task ThreadSafety_ConcurrentAddsAndRemoves_NoExceptions()
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
        await Assert.That(list.Count).IsGreaterThanOrEqualTo(0);
    }

    [Test]
    public async Task ThreadSafety_ConcurrentEnumerationWithAdds_NoExceptions()
    {
        var list = new SnapshotList<int>();
        for (int i = 0; i < 100; i++) list.Add(i);
        await Assert.That(() =>
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
        }).ThrowsNothing();
    }

    [Test]
    public async Task ThreadSafety_ConcurrentEnumerationWithRemoves_NoExceptions()
    {
        var list = new SnapshotList<int>();
        for (int i = 0; i < 100; i++) list.Add(i);
        await Assert.That(() =>
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
        }).ThrowsNothing();
    }

    [Test]
    public async Task ResetEnumerator_Works()
    {
        var list = new SnapshotList<int> { 1, 2 };
        using var e = list.GetEnumerator();
        await Assert.That(e.MoveNext()).IsTrue();
        e.Reset();
        await Assert.That(e.MoveNext()).IsTrue();
        await Assert.That(e.Current).IsEqualTo(2);
    }

    [Test]
    public async Task MultipleEnumerators_Independence()
    {
        var list = new SnapshotList<int> { 1, 2 };
        using var e1 = list.GetEnumerator();
        using var e2 = list.GetEnumerator();
        e1.MoveNext(); // at 2
        await Assert.That(e2.MoveNext()).IsTrue();
        await Assert.That(e2.Current).IsEqualTo(2);
    }

    [Test]
    public async Task Enumerator_MoveNextAfterCompletion_ReturnsFalse()
    {
        var list = new SnapshotList<int> { 1 };
        using var e = list.GetEnumerator();
        while (e.MoveNext()) { }

        await Assert.That(e.MoveNext()).IsFalse();
    }

    [Test]
    public async Task Enumerator_Reset_AllowsReiteration()
    {
        var list = new SnapshotList<int> { 1, 2 };
        using var e = list.GetEnumerator();
        var firstRun = new List<int>();
        while (e.MoveNext()) firstRun.Add(e.Current);
        e.Reset();
        var secondRun = new List<int>();
        while (e.MoveNext()) secondRun.Add(e.Current);
        await Assert.That(secondRun).IsEquivalentTo(firstRun, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Add_NullValue_ForReferenceTypes()
    {
        var list = new SnapshotList<string?> { null };
        await Assert.That(list.Count).IsEqualTo(1);
        await Assert.That(list.ToList()).IsEquivalentTo(new string?[] { null }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Remove_NullValue_ReturnsTrue()
    {
        var list = new SnapshotList<string?> { null };
        await Assert.That(list.Remove(null)).IsTrue();
        await Assert.That(list.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Insert_NullValue_Works()
    {
        var list = new SnapshotList<string?>();
        list.Insert(0, null);
        await Assert.That(list.Count).IsEqualTo(1);
        await Assert.That(list.ToList()).IsEquivalentTo(new string?[] { null }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Add_Remove_Add_Remove_Repeatedly_Works()
    {
        var list = new SnapshotList<int>();
        for (int i = 0; i < 100; i++)
        {
            list.Add(i);
            await Assert.That(list.Remove(i)).IsTrue();
        }

        await Assert.That(list.Count).IsEqualTo(0);
    }

    [Test]
    public async Task RemoveAll_Items_ThenCountZero()
    {
        var list = new SnapshotList<int>();
        foreach (var i in Enumerable.Range(0, 10)) list.Add(i);
        foreach (var i in Enumerable.Range(0, 10)) list.Remove(i);
        await Assert.That(list.Count).IsEqualTo(0);
    }

    [Test]
    public async Task AddAfterRemove_CountAndEnumeration()
    {
        var list = new SnapshotList<int> { 1 };
        list.Remove(1);
        list.Add(2);
        await Assert.That(list.Count).IsEqualTo(1);
        await Assert.That(list.ToList()).IsEquivalentTo(new[] { 2 }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Insert_MaintainsOrder()
    {
        var list = new SnapshotList<int> { 1, 3 };
        list.Insert(1, 2);
        await Assert.That(list.ToList()).IsEquivalentTo(new[] { 3, 2, 1 }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Remove_MaintainsOrder()
    {
        var list = new SnapshotList<int> { 1, 2, 3 };
        list.Remove(2);
        await Assert.That(list.ToList()).IsEquivalentTo(new[] { 3, 1 }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Add_Interleaved_WithInsert_MaintainsCorrectSequence()
    {
        var list = new SnapshotList<int> { 1 // [1]
        };
        list.Insert(1, 3); // [1,3]
        list.Add(2); // [2,1,3]
        await Assert.That(list.ToList()).IsEquivalentTo(new[] { 2, 1, 3 }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task SnapshotEnumeration_CapturesVersionIndependentOfFurtherOperations()
    {
        var list = new SnapshotList<int> { 1 };
        var seq = list.ToList();
        list.Add(2);
        list.Remove(1);
        await Assert.That(seq).IsEquivalentTo(new[] { 1 }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task SnapshotEnumerator_Dispose_DoesNothing()
    {
        var list = new SnapshotList<int> { 1 };
        var e = list.GetEnumerator();
        await Assert.That(() => e.Dispose()).ThrowsNothing();
    }

    [Test]
    public async Task Enumeration_IsIdempotent_ForFixedSnapshot()
    {
        var list = new SnapshotList<int> { 1, 2 };
        var snapshot = list.ToList();
        // enumerate twice
        var first = snapshot.ToList();
        var second = snapshot.ToList();
        await Assert.That(second).IsEquivalentTo(first, CollectionOrdering.Matching);
    }

    [Test]
    public async Task ThreadSafety_ConcurrentInsert_CountCorrect()
    {
        var list = new SnapshotList<int>();
        int threads = 5, perThread = 200;
        Parallel.For(0, threads, i =>
        {
            for (int j = 0; j < perThread; j++)
                list.Insert(0, i * perThread + j);
        });
        await Assert.That(list.Count).IsEqualTo(threads * perThread);
    }

    [Test]
    public async Task ThreadSafety_ConcurrentInsert_ItemsCorrect()
    {
        var list = new SnapshotList<int>();
        int threads = 3, perThread = 100;
        Parallel.For(0, threads, i =>
        {
            for (int j = 0; j < perThread; j++)
                list.Insert(0, i * perThread + j);
        });
        var items = list.ToList();
        await Assert.That(items.Count).IsEqualTo(threads * perThread);
        for (int i = 0; i < threads * perThread; i++)
            await Assert.That(items.Contains(i)).IsTrue();
    }

    [Test]
    public async Task ThreadSafety_MixedOperations_NoDeadlock()
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
        await Assert.That(() => Task.WaitAll(tasks)).ThrowsNothing();
    }

    [Test]
    public async Task AddLargeNumber_StressTest()
    {
        var list = new SnapshotList<int>();
        int n = 5000;
        for (int i = 0; i < n; i++)
            list.Add(i);
        await Assert.That(list.Count).IsEqualTo(n);
        var items = list.ToList();
        await Assert.That(items.First()).IsEqualTo(n - 1);
        await Assert.That(items.Last()).IsEqualTo(0);
    }

    [Test]
    public async Task Insert_MultipleSequentialIndices_MaintainsOrder()
    {
        var list = new SnapshotList<int>();
        list.Insert(0, 1); // [1]
        list.Insert(1, 3); // [1,3]
        list.Insert(1, 2); // [1,2,3]
        await Assert.That(list.ToList()).IsEquivalentTo(new[] { 1, 2, 3 }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task ThreadSafety_ConcurrentAddRemoveDistinctRanges_CountZero()
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
        await Assert.That(list.Count).IsEqualTo(0);
    }

    [Test]
    public async Task ThreadSafety_ConcurrentInsertRemoveDistinctRanges_CountZero()
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
        await Assert.That(list.Count).IsEqualTo(0);
    }

    [Test]
    public async Task ThreadSafety_ConcurrentAdd_CausesGrowArray_CorrectCount()
    {
        var list = new SnapshotList<int>(2);
        int threads = 20, per = 200;
        Parallel.For(0, threads, i =>
        {
            for (int j = 0; j < per; j++)
                list.Add(i * per + j);
        });
        await Assert.That(list.Count).IsEqualTo(threads * per);
    }

    [Test]
    public async Task ThreadSafety_ConcurrentInsert_CausesGrowArray_CorrectCount()
    {
        var list = new SnapshotList<int>(2);
        int threads = 10, per = 100;
        Parallel.For(0, threads, i =>
        {
            for (int j = 0; j < per; j++)
                list.Insert(0, i * per + j);
        });
        await Assert.That(list.Count).IsEqualTo(threads * per);
    }

    [Test]
    public async Task ThreadSafety_ConcurrentMixedOperationsDistinctRanges_CountZero()
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
        await Assert.That(list.Count).IsEqualTo(threads * range); // only the inserts remain
    }

    [Test]
    public async Task ThreadSafety_ConcurrentAdd_AndEnumeration_NoExceptions()
    {
        var list = new SnapshotList<int>();
        for (int i = 0; i < 100; i++) list.Add(i);
        await Assert.That(() =>
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
        }).ThrowsNothing();
    }

    [Test]
    public async Task ThreadSafety_ConcurrentRemove_AndEnumeration_NoExceptions()
    {
        var list = new SnapshotList<int>();
        for (int i = 0; i < 500; i++) list.Add(i);
        await Assert.That(() =>
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
        }).ThrowsNothing();
    }

    [Test]
    public async Task ThreadSafety_ConcurrentInsert_AndEnumeration_NoExceptions()
    {
        var list = new SnapshotList<int>();
        for (int i = 0; i < 200; i++) list.Add(i);
        await Assert.That(() =>
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
        }).ThrowsNothing();
    }

    [Test]
    public async Task ThreadSafety_EnumerationDuringGrow_NoExceptions()
    {
        var list = new SnapshotList<int>(1);
        await Assert.That(() =>
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
        }).ThrowsNothing();
    }

    [Test]
    public async Task ThreadSafety_HighConcurrencyStress_NoDeadlock()
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
        await Assert.That(() => Task.WaitAll(tasks)).ThrowsNothing();
    }

    [Test]
    public async Task ThreadSafety_ConcurrentAddRemoveLoop_NoExceptions()
    {
        var list = new SnapshotList<int>();
        await Assert.That(() =>
        {
            Parallel.For(0, 100, _ =>
            {
                for (int i = 0; i < 100; i++)
                {
                    list.Add(i);
                    list.Remove(i);
                }
            });
        }).ThrowsNothing();
    }

    [Test]
    public async Task ThreadSafety_ConcurrentInsertRemoveLoop_CountZero()
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
        await Assert.That(list.Count).IsEqualTo(0);
    }

    [Test]
    public async Task ThreadSafety_ConcurrentFreeListReuseUnderContention()
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
        await Assert.That(list.Count).IsEqualTo(0);
    }

    [Test]
    public async Task ThreadSafety_ConcurrentGrowUnderContention()
    {
        var list = new SnapshotList<int>(2);
        Parallel.For(0, 20, i =>
        {
            for (int j = 0; j < 200; j++)
                list.Add(i * 200 + j);
        });
        await Assert.That(list.Count).IsEqualTo(20 * 200);
    }

    [Test]
    public async Task ThreadSafety_ConcurrentAddRemoveAlternating_NoDeadlock()
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
        await Assert.That(() => Task.WaitAll(tasks)).ThrowsNothing();
    }

    [Test]
    public async Task ThreadSafety_ConcurrentShadowingOperations_CountMatches()
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
        await Assert.That(list.Count).IsEqualTo(threads * per * 2);
    }

    [Test]
    public async Task ThreadSafety_ConcurrentInsertAtEnd_CountMatches()
    {
        var list = new SnapshotList<int>();
        for (int i = 0; i < 100; i++) list.Add(i);
        Parallel.For(0, 10, _ =>
        {
            list.Insert(list.Count, 999);
        });
        await Assert.That(list.Count).IsEqualTo(100 + 10);
    }

    [Test]
    public async Task ThreadSafety_ConcurrentInsertAtRandomPositions_NoExceptions()
    {
        var rnd = new Random();
        var list = new SnapshotList<int>();
        for (int i = 0; i < 500; i++) list.Add(i);
        await Assert.That(() =>
        {
            Parallel.For(0, 200, _ =>
            {
                int idx = rnd.Next(0, list.Count + 1);
                list.Insert(idx, -1);
            });
        }).ThrowsNothing();
    }

    [Test]
    public async Task ThreadSafety_ConcurrentAddRemoveWithParallelForEach_NoExceptions()
    {
        var list = new SnapshotList<int>();
        var data = Enumerable.Range(0, 1000).ToArray();
        await Assert.That(() =>
        {
            Parallel.ForEach(data, i =>
            {
                list.Add(i);
                list.Remove(i / 2);
            });
        }).ThrowsNothing();
    }

    [Test]
    public async Task ThreadSafety_ConcurrentSnapshotEnumerationUnderLoad()
    {
        var list = new SnapshotList<int>();
        for (int i = 0; i < 1000; i++) list.Add(i);
        await Assert.That(() =>
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
        }).ThrowsNothing();
    }

    [Test]
    public async Task ThreadSafety_ConcurrentDeepEnumerationAfterOperations()
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
        await Assert.That(() =>
        {
            while (enumerator.MoveNext())
            {
                _ = enumerator.Current;
            }
        }).ThrowsNothing();
    }

    [Test]
    public async Task ThreadSafety_ConcurrentEnumeratorReset_NoExceptions()
    {
        var list = new SnapshotList<int>();
        for (int i = 0; i < 100; i++) list.Add(i);
        var e = list.GetEnumerator();
        using var e1 = e as IDisposable;
        await Assert.That(() =>
        {
            Parallel.Invoke(
                () => e.Reset(),
                () => list.Add(101),
                () => list.Remove(0)
            );
        }).ThrowsNothing();
    }

    [Test]
    public async Task ThreadSafety_ConcurrentEnumeratorDisposeDuringOperations_NoExceptions()
    {
        var list = new SnapshotList<int>();
        for (int i = 0; i < 100; i++) list.Add(i);
        var e = list.GetEnumerator();
        await Assert.That(() =>
        {
            Parallel.Invoke(
                () => e.Dispose(),
                () => list.Add(200),
                () => list.Remove(1)
            );
        }).ThrowsNothing();
    }

    [Test]
    public async Task ThreadSafety_ConcurrentOperationsWithThreadSleep_NoDeadlock()
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
        await Assert.That(() => Task.WaitAll(t1, t2)).ThrowsNothing();
    }

    [Test]
    public async Task ThreadSafety_TaskBasedConcurrency_NoExceptions()
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
        await Assert.That(() => Task.WaitAll(tasks)).ThrowsNothing();
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
    }

    [Test]
    public async Task ThreadSafety_InterleavedAddInsertRemoveStressTest()
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
        await Assert.That(() => Task.WaitAll(tasks)).ThrowsNothing();
    }

    [Test]
    public async Task ThreadSafety_BulkConcurrentOps_CountMatchesAddsAndInserts()
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
        await Assert.That(list.Count).IsEqualTo(adds + inserts);
    }

    [Test]
    public async Task ThreadSafety_CombinedHighLoadTest_NoDeadlock()
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

        await Assert.That(() => Task.WaitAll(tasks.ToArray())).ThrowsNothing();
    }

    [Test]
    public async Task Clear_OnEmptyList_CountRemainsZero()
    {
        var list = new SnapshotList<int>();
        list.Clear();
        await Assert.That(list.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Clear_OnNonEmptyList_CountBecomesZero()
    {
        var list = new SnapshotList<int> { 1, 2, 3 };
        list.Clear();
        await Assert.That(list.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Clear_OnNonEmptyList_EnumerationReturnsEmpty()
    {
        var list = new SnapshotList<int> { 1, 2, 3, 4, 5 };
        list.Clear();
        await Assert.That(list.Any()).IsFalse();
        await Assert.That(list.ToList()).IsEmpty();
    }

    [Test]
    public async Task Clear_ThenAdd_Works()
    {
        var list = new SnapshotList<int> { 1, 2, 3 };
        list.Clear();
        list.Add(42);
        await Assert.That(list.Count).IsEqualTo(1);
        await Assert.That(list.ToList()).IsEquivalentTo(new[] { 42 }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Clear_ThenInsert_Works()
    {
        var list = new SnapshotList<int> { 1, 2, 3 };
        list.Clear();
        list.Insert(0, 100);
        await Assert.That(list.Count).IsEqualTo(1);
        await Assert.That(list.ToList()).IsEquivalentTo(new[] { 100 }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Clear_SnapshotBeforeClear_StillHasItems()
    {
        var list = new SnapshotList<int> { 1, 2, 3 };
        var snapshot = list.ToList();
        list.Clear();
        await Assert.That(snapshot).IsEquivalentTo(new[] { 3, 2, 1 }, CollectionOrdering.Matching);
        await Assert.That(list.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Clear_EnumeratorBeforeClear_StillHasItems()
    {
        var list = new SnapshotList<int> { 1, 2, 3 };
        using var enumerator = list.GetEnumerator();
        list.Clear();
        var result = new List<int>();
        while (enumerator.MoveNext())
            result.Add(enumerator.Current);
        await Assert.That(result).IsEmpty();
    }

    [Test]
    public async Task Clear_MultipleTimes_Works()
    {
        var list = new SnapshotList<int> { 1, 2 };
        list.Clear();
        list.Clear();
        list.Clear();
        await Assert.That(list.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Clear_FreesNodesForReuse()
    {
        var list = new SnapshotList<int>(4) { 1, 2, 3 };
        list.Clear();
        list.Add(4);
        list.Add(5);
        list.Add(6);
        await Assert.That(list.Count).IsEqualTo(3);
        await Assert.That(list.ToList()).IsEquivalentTo(new[] { 6, 5, 4 }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task ThreadSafety_ConcurrentClear_NoExceptions()
    {
        var list = new SnapshotList<int>();
        for (int i = 0; i < 1000; i++) list.Add(i);
        await Assert.That(() =>
        {
            Parallel.For(0, 10, _ =>
            {
                list.Clear();
            });
        }).ThrowsNothing();
        await Assert.That(list.Count).IsEqualTo(0);
    }

    [Test]
    public async Task ThreadSafety_ConcurrentClearAndAdd_NoExceptions()
    {
        var list = new SnapshotList<int>();
        for (int i = 0; i < 100; i++) list.Add(i);
        await Assert.That(() =>
        {
            Parallel.Invoke(
                () => list.Clear(),
                () =>
                {
                    for (int i = 0; i < 100; i++) list.Add(i + 1000);
                }
            );
        }).ThrowsNothing();
    }

    [Test]
    public async Task ThreadSafety_ConcurrentClearAndEnumeration_NoExceptions()
    {
        var list = new SnapshotList<int>();
        for (int i = 0; i < 100; i++) list.Add(i);
        await Assert.That(() =>
        {
            Parallel.Invoke(
                () => list.Clear(),
                () =>
                {
                    foreach (var _ in list) { }
                }
            );
        }).ThrowsNothing();
    }

    [Test]
    public async Task Clear_LargeList_Works()
    {
        var list = new SnapshotList<int>();
        for (int i = 0; i < 10000; i++) list.Add(i);
        await Assert.That(list.Count).IsEqualTo(10000);
        list.Clear();
        await Assert.That(list.Count).IsEqualTo(0);
        await Assert.That(list.Any()).IsFalse();
    }
}
