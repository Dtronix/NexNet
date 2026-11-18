using NexNet.Internals.Collections.Lists;
using System.Collections.Concurrent;
using System.Threading.Tasks;

namespace NexNet.IntegrationTests.Collections;

internal class IndexedCircularListTests
{
    [Test]
    public async Task Constructor_DefaultCapacity_InitialCountIsZero()
    {
        var list = new IndexedCircularList<string>();
        await Assert.That(list.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Constructor_WithCapacity_InitialCountIsZeroAndIndices()
    {
        var list = new IndexedCircularList<string>(5);
        await Assert.That(list.Count).IsEqualTo(0);
        await Assert.That(list.FirstIndex).IsEqualTo(0);
        await Assert.That(list.LastIndex).IsEqualTo(-1);
    }

    [Test]
    public async Task Constructor_WithZeroCapacity_ThrowsArgumentException()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => new IndexedCircularList<string>(0));
    }

    [Test]
    public async Task Constructor_WithNegativeCapacity_ThrowsArgumentException()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => new IndexedCircularList<string>(-1));
    }

    [Test]
    public async Task Add_SingleItem_ReturnsZeroIndex()
    {
        var list = new IndexedCircularList<string>();
        var index = list.Add("A");
        await Assert.That(index).IsEqualTo(0);
    }

    [Test]
    public async Task Add_MultipleItems_ReturnsIncrementalIndices()
    {
        var list = new IndexedCircularList<string>();
        var idx1 = list.Add("A");
        var idx2 = list.Add("B");
        await Assert.That(idx1).IsEqualTo(0);
        await Assert.That(idx2).IsEqualTo(1);
    }

    [Test]
    public async Task Add_UnderCapacity_IncreasesCount()
    {
        var list = new IndexedCircularList<string>(3);
        list.Add("A");
        list.Add("B");
        await Assert.That(list.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Add_AtCapacity_DoesNotIncreaseCountBeyondCapacity()
    {
        var list = new IndexedCircularList<string>(2);
        list.Add("A");
        list.Add("B");
        list.Add("C");
        await Assert.That(list.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Count_AfterAdds_ReturnsCorrectCount()
    {
        var list = new IndexedCircularList<string>(4);
        list.Add("A");
        list.Add("B");
        list.Add("C");
        await Assert.That(list.Count).IsEqualTo(3);
    }

    [Test]
    public async Task FirstIndex_EmptyList_ReturnsZero()
    {
        var list = new IndexedCircularList<string>();
        await Assert.That(list.FirstIndex).IsEqualTo(0);
    }

    [Test]
    public async Task LastIndex_EmptyList_ReturnsMinusOne()
    {
        var list = new IndexedCircularList<string>();
        await Assert.That(list.LastIndex).IsEqualTo(-1);
    }

    [Test]
    public async Task FirstIndex_AfterAddsUnderCapacity()
    {
        var list = new IndexedCircularList<string>(5);
        list.Add("A");
        list.Add("B");
        await Assert.That(list.FirstIndex).IsEqualTo(0);
    }

    [Test]
    public async Task LastIndex_AfterAddsUnderCapacity()
    {
        var list = new IndexedCircularList<string>(5);
        list.Add("A");
        list.Add("B");
        await Assert.That(list.LastIndex).IsEqualTo(1);
    }

    [Test]
    public async Task FirstIndex_AfterAtCapacity()
    {
        var list = new IndexedCircularList<string>(3);
        list.Add("A");
        list.Add("B");
        list.Add("C");
        await Assert.That(list.FirstIndex).IsEqualTo(0);
    }

    [Test]
    public async Task LastIndex_AfterAtCapacity()
    {
        var list = new IndexedCircularList<string>(3);
        list.Add("A");
        list.Add("B");
        list.Add("C");
        await Assert.That(list.LastIndex).IsEqualTo(2);
    }

    [Test]
    public async Task FirstIndex_AfterOverCapacity()
    {
        var list = new IndexedCircularList<string>(3);
        list.Add("A");
        list.Add("B");
        list.Add("C");
        list.Add("D");
        await Assert.That(list.FirstIndex).IsEqualTo(1);
    }

    [Test]
    public async Task LastIndex_AfterOverCapacity()
    {
        var list = new IndexedCircularList<string>(3);
        list.Add("A");
        list.Add("B");
        list.Add("C");
        list.Add("D");
        await Assert.That(list.LastIndex).IsEqualTo(3);
    }

    [Test]
    public async Task Indexer_Get_ValidIndexUnderCapacity()
    {
        var list = new IndexedCircularList<string>(3);
        list.Add("A");
        list.Add("B");
        list.Add("C");
        await Assert.That(list[1]).IsEqualTo("B");
    }

    [Test]
    public async Task Indexer_Get_ValidIndexAtCapacity()
    {
        var list = new IndexedCircularList<string>(3);
        list.Add("A");
        list.Add("B");
        list.Add("C");
        await Assert.That(list[2]).IsEqualTo("C");
    }

    [Test]
    public async Task Indexer_Get_ValidIndexAfterWrap()
    {
        var list = new IndexedCircularList<string>(3);
        list.Add("A");
        list.Add("B");
        list.Add("C");
        list.Add("D");
        await Assert.That(list[3]).IsEqualTo("D");
        await Assert.That(list[2]).IsEqualTo("C");
    }

    [Test]
    public async Task Indexer_Get_IndexBelowFirst_ThrowsException()
    {
        var list = new IndexedCircularList<string>(3);
        list.Add("A");
        list.Add("B");
        list.Add("C");
        list.Add("D");
        await Assert.ThrowsAsync<IndexOutOfRangeException>(() =>
        {
            var _ = list[0];
        });
    }

    [Test]
    public async Task Indexer_Get_IndexAboveLast_ThrowsException()
    {
        var list = new IndexedCircularList<string>(3);
        list.Add("A");
        list.Add("B");
        await Assert.ThrowsAsync<IndexOutOfRangeException>(() =>
        {
            var _ = list[2];
        });
    }

    [Test]
    public async Task ValidateIndex_ValidIndex_ReturnsTrue()
    {
        var list = new IndexedCircularList<string>(3);
        list.Add("A");
        list.Add("B");
        await Assert.That(list.ValidateIndex(1)).IsTrue();
    }

    [Test]
    public async Task ValidateIndex_IndexBelowFirst_ReturnsFalse()
    {
        var list = new IndexedCircularList<string>(3);
        list.Add("A");
        list.Add("B");
        await Assert.That(list.ValidateIndex(-1)).IsFalse();
    }

    [Test]
    public async Task ValidateIndex_IndexAboveLast_ReturnsFalse()
    {
        var list = new IndexedCircularList<string>(3);
        list.Add("A");
        list.Add("B");
        await Assert.That(list.ValidateIndex(2)).IsFalse();
    }

    [Test]
    public async Task TryGetValue_ValidIndex_ReturnsTrueAndValue()
    {
        var list = new IndexedCircularList<string?>(3);
        list.Add("A");
        list.Add("B");
        string? value;
        var result = list.TryGetValue(1, out value);
        await Assert.That(result).IsTrue();
        await Assert.That(value).IsEqualTo("B");
    }

    [Test]
    public async Task TryGetValue_IndexBelowFirst_ReturnsFalseAndNull()
    {
        var list = new IndexedCircularList<string?>(3);
        list.Add("A");
        string? value;
        var result = list.TryGetValue(-1, out value);
        await Assert.That(result).IsFalse();
        await Assert.That(value).IsNull();
    }

    [Test]
    public async Task TryGetValue_IndexAboveLast_ReturnsFalseAndNull()
    {
        var list = new IndexedCircularList<string?>(3);
        list.Add("A");
        string? value;
        var result = list.TryGetValue(1, out value);
        await Assert.That(result).IsFalse();
        await Assert.That(value).IsNull();
    }

    [Test]
    public async Task Clear_OnNonEmpty_ListBecomesEmpty()
    {
        var list = new IndexedCircularList<string>(3);
        list.Add("A");
        list.Add("B");
        list.Clear();
        await Assert.That(list.Count).IsEqualTo(0);
        await Assert.That(list.FirstIndex).IsEqualTo(2);

        // One less than the FirstIndex to indicate a negative delta.
        await Assert.That(list.LastIndex).IsEqualTo(1);
    }

    [Test]
    public async Task Clear_ClearsBuffer()
    {
        var list = new IndexedCircularList<string?>(3);
        list.Add("A");
        list.Add("B");
        list.Clear();
        string? value;
        var result = list.TryGetValue(0, out value);
        await Assert.That(result).IsFalse();
        await Assert.That(value).IsNull();
    }

    [Test]
    public async Task Reset_DefaultThenAdd_SetsNextIndexToZero()
    {
        var list = new IndexedCircularList<string>(3);
        list.Add("A");
        list.Add("B");
        list.Reset();
        await Assert.That(list.Count).IsEqualTo(0);
        await Assert.That(list.FirstIndex).IsEqualTo(0);
        await Assert.That(list.LastIndex).IsEqualTo(-1);
        list.Add("C");
        await Assert.That(list.LastIndex).IsEqualTo(0);
    }

    [Test]
    public async Task Reset_WithCustomNextIndex_SetsNextIndex()
    {
        var list = new IndexedCircularList<string>(3);
        list.Add("A");
        list.Reset(5);
        await Assert.That(list.Count).IsEqualTo(0);
        await Assert.That(list.FirstIndex).IsEqualTo(5);
        await Assert.That(list.LastIndex).IsEqualTo(4);
        list.Add("B");
        await Assert.That(list.LastIndex).IsEqualTo(5);
        await Assert.That(list[5]).IsEqualTo("B");
    }

    [Test]
    public async Task Reset_ClearsBufferContents()
    {
        var list = new IndexedCircularList<string>(3);
        list.Add("A");
        list.Reset();
        await Assert.ThrowsAsync<IndexOutOfRangeException>(() =>
        {
            var _ = list[0];
        });
    }

    [Test]
    public async Task Enumeration_EmptyList_YieldsNoElements()
    {
        var list = new IndexedCircularList<string>(3);
        var items = list.ToList();
        await Assert.That(items).IsEmpty();
    }

    [Test]
    public async Task Enumeration_UnderCapacity_YieldsItemsInOrder()
    {
        var list = new IndexedCircularList<string>(3);
        list.Add("A");
        list.Add("B");
        list.Add("C");
        var items = list.ToList();
        await Assert.That(items.Select(t => t.Item)).IsEqualTo(new[] { "A", "B", "C" });
        await Assert.That(items.Select(t => t.Index)).IsEqualTo(new long[] { 0, 1, 2 });
    }

    [Test]
    public async Task Enumeration_AfterWrap_YieldsItemsInOrder()
    {
        var list = new IndexedCircularList<string>(3);
        list.Add("A");
        list.Add("B");
        list.Add("C");
        list.Add("D");
        list.Add("E");
        var items = list.ToList();
        await Assert.That(items.Select(t => t.Item)).IsEqualTo(new[] { "C", "D", "E" });
        await Assert.That(items.Select(t => t.Index)).IsEqualTo(new long[] { 2, 3, 4 });
    }

    [Test]
    public async Task ConsecutiveOperations_AddClearAdd_CountAndIndicesCorrect()
    {
        var list = new IndexedCircularList<string>(3);
        list.Add("A");
        list.Add("B");
        list.Clear();
        list.Add("C");
        await Assert.That(list.Count).IsEqualTo(1);
        await Assert.That(list.FirstIndex).IsEqualTo(2);
        await Assert.That(list.LastIndex).IsEqualTo(2);
    }

    [Test]
    public async Task ConsecutiveOperations_AddResetAdd_CountAndIndicesCorrect()
    {
        var list = new IndexedCircularList<string>(3);
        list.Add("A");
        list.Add("B");
        list.Reset(10);
        list.Add("C");
        list.Add("D");
        await Assert.That(list.FirstIndex).IsEqualTo(10);
        await Assert.That(list.LastIndex).IsEqualTo(11);
    }

    [Test]
    public async Task Add_NullValueAllowed()
    {
        var list = new IndexedCircularList<string?>(3);
        var idx = list.Add(null);
        await Assert.That(idx).IsEqualTo(0);
        await Assert.That(list[0]).IsNull();
    }

    [Test]
    public async Task TryGetValue_NullStored_ReturnsTrueAndNull()
    {
        var list = new IndexedCircularList<string?>(3);
        list.Add(null);
        string? value;
        var result = list.TryGetValue(0, out value);
        await Assert.That(result).IsTrue();
        await Assert.That(value).IsNull();
    }

    [Test]
    public async Task Indexer_NullStored_ReturnsNull()
    {
        var list = new IndexedCircularList<string?>(3);
        list.Add(null);
        await Assert.That(list[0]).IsNull();
    }

    [Test]
    public async Task ValidateIndex_EmptyList_ReturnsFalse()
    {
        var list = new IndexedCircularList<string>(3);
        await Assert.That(list.ValidateIndex(0)).IsFalse();
    }

    [Test]
    public async Task ValidateIndex_AfterWrap_CorrectTrueFalse()
    {
        var list = new IndexedCircularList<string>(3);
        list.Add("A");
        list.Add("B");
        list.Add("C");
        list.Add("D");
        await Assert.That(list.ValidateIndex(0)).IsFalse();
        await Assert.That(list.ValidateIndex(1)).IsTrue();
        await Assert.That(list.ValidateIndex(2)).IsTrue();
        await Assert.That(list.ValidateIndex(3)).IsTrue();
        await Assert.That(list.ValidateIndex(4)).IsFalse();
    }

    /*
    [Test]
    public void IEnumerator_NonGenericMatchesGeneric()
    {
        var list = new IndexedCircularList<string>(3);
        list.Add("A");
        list.Add("B");
        var gen = list.GetEnumerator();
        gen.MoveNext();
        var firstGen = gen.Current;
        var nonGen = ((IEnumerable)list).GetEnumerator();
        nonGen.MoveNext();
        var firstNonGen = ((ValueTuple<long, string>)nonGen.Current);
        Assert.That(firstNonGen, Is.EqualTo(firstGen));
    }*/

    [Test]
    public async Task BufferModuloMappingIndexingBehavior()
    {
        var list = new IndexedCircularList<string>(3);
        list.Add("A");
        list.Add("B");
        list.Add("C");
        list.Add("D");
        bool ok = list.TryGetValue(3, out var val);
        await Assert.That(ok).IsTrue();
        await Assert.That(val).IsEqualTo("D");
        await Assert.That(list.ValidateIndex(0)).IsFalse();
    }

    [Test]
    public async Task ManyWraps_Enumeration_OnlyLastNItems()
    {
        var list = new IndexedCircularList<string>(3);
        for (int i = 0; i < 10; i++)
            list.Add($"X{i}");
        var items = list.ToList();
        await Assert.That(items.Count).IsEqualTo(3);
        await Assert.That(items.Select(t => t.Index)).IsEqualTo(new long[] { 7, 8, 9 });
    }

    [Test]
    public async Task FirstAndLastIndex_MonotonicBehavior()
    {
        var list = new IndexedCircularList<string>(3);
        for (int i = 0; i < 5; i++)
        {
            var idx = list.Add($"X{i}");
            await Assert.That(list.LastIndex).IsEqualTo(idx);
        }

        await Assert.That(list.FirstIndex).IsEqualTo(list.LastIndex - list.Count + 1);
    }

    [Test]
    public async Task FirstAndLastIndex_AfterResetThenAdd()
    {
        var list = new IndexedCircularList<string>(4);
        list.Add("A");
        list.Add("B");
        list.Reset(100);
        await Assert.That(list.FirstIndex).IsEqualTo(100);
        await Assert.That(list.LastIndex).IsEqualTo(99);
        list.Add("C");
        await Assert.That(list.FirstIndex).IsEqualTo(100);
        await Assert.That(list.LastIndex).IsEqualTo(100);
    }

    [Test]
    public async Task CapacityOne_WrapBehavior()
    {
        var list = new IndexedCircularList<string>(1);
        var idx1 = list.Add("A");
        var idx2 = list.Add("B");
        await Assert.That(list.Count).IsEqualTo(1);
        await Assert.That(list.FirstIndex).IsEqualTo(1);
        await Assert.That(list.LastIndex).IsEqualTo(1);
        await Assert.That(list[1]).IsEqualTo("B");
    }

    [Test]
    public async Task CapacityTwo_WrapTwice_Behavior()
    {
        var list = new IndexedCircularList<string>(2);
        list.Add("A");
        list.Add("B");
        list.Add("C");
        list.Add("D");
        var items = list.ToList();
        await Assert.That(items.Select(t => t.Index)).IsEqualTo(new long[] { 2, 3 });
        await Assert.That(items.Select(t => t.Item)).IsEqualTo(new[] { "C", "D" });
    }

    [Test]
    public async Task ValidateIndex_AfterClear_ReturnsFalse()
    {
        var list = new IndexedCircularList<string>(3);
        list.Add("A");
        list.Clear();
        await Assert.That(list.ValidateIndex(0)).IsFalse();
    }

    [Test]
    public async Task TryGetValue_AfterReset_ReturnsFalseForOldIndex()
    {
        var list = new IndexedCircularList<string>(3);
        list.Add("A");
        list.Reset();
        await Assert.That(list.TryGetValue(0, out _)).IsFalse();
    }

    [Test]
    public async Task ConcurrentAdds_AllIndicesAreUnique()
    {
        var list = new IndexedCircularList<string>(10000);
        var indices = new ConcurrentBag<long>();
        var threadCount = 10;
        var addsPerThread = 1000;

        var tasks = Enumerable.Range(0, threadCount).Select(t => Task.Run(() =>
        {
            for (int i = 0; i < addsPerThread; i++)
            {
                var index = list.Add($"Thread{t}-Item{i}");
                indices.Add(index);
            }
        }));

        Task.WaitAll(tasks.ToArray());

        // All indices should be unique
        var uniqueIndices = indices.Distinct().ToList();
        await Assert.That(uniqueIndices.Count).IsEqualTo(indices.Count).Because($"Expected {indices.Count} unique indices but got {uniqueIndices.Count}");
    }

    [Test]
    public async Task ConcurrentAddAndRead_NoExceptions()
    {
        var list = new IndexedCircularList<string>(100);
        var exceptions = new ConcurrentBag<Exception>();
        var cts = new CancellationTokenSource();
        var addsCompleted = 0;

        // Writer task
        var writerTask = Task.Run(() =>
        {
            try
            {
                for (int i = 0; i < 1000; i++)
                {
                    list.Add($"Item{i}");
                    Interlocked.Increment(ref addsCompleted);
                }
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
            finally
            {
                cts.Cancel();
            }
        });

        // Reader tasks
        var readerTasks = Enumerable.Range(0, 5).Select(_ => Task.Run(() =>
        {
            try
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    var count = list.Count;
                    if (count > 0)
                    {
                        var firstIdx = list.FirstIndex;
                        var lastIdx = list.LastIndex;

                        // Try to read valid indices
                        for (long i = firstIdx; i <= lastIdx && i < firstIdx + 10; i++)
                        {
                            list.TryGetValue(i, out string? _);
                        }
                    }
                    Thread.Yield();
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                exceptions.Add(ex);
            }
        }));

        Task.WaitAll(new[] { writerTask }.Concat(readerTasks).ToArray());

        await Assert.That(exceptions).IsEmpty().Because($"Concurrent operations threw exceptions: {string.Join(", ", exceptions.Select(e => e.Message))}");
    }

    [Test]
    public async Task ConcurrentAddAndClear_NoExceptions()
    {
        var list = new IndexedCircularList<string>(50);
        var exceptions = new ConcurrentBag<Exception>();
        var iterations = 100;

        var tasks = new List<Task>
        {
            // Adder
            Task.Run(() =>
            {
                try
                {
                    for (int i = 0; i < iterations * 10; i++)
                    {
                        list.Add($"Item{i}");
                    }
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }
            }),
            // Clearer
            Task.Run(() =>
            {
                try
                {
                    for (int i = 0; i < iterations; i++)
                    {
                        Thread.Sleep(1);
                        list.Clear();
                    }
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }
            })
        };

        Task.WaitAll(tasks.ToArray());

        await Assert.That(exceptions).IsEmpty().Because($"Concurrent Add/Clear threw exceptions: {string.Join(", ", exceptions.Select(e => e.Message))}");
    }
}