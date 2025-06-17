using NexNet.Internals.Collections.Lists;
using NUnit.Framework;

namespace NexNet.IntegrationTests.Collections;

internal class IndexedCircularListTests
{
    [Test]
    public void Constructor_DefaultCapacity_InitialCountIsZero()
    {
        var list = new IndexedCircularList<string>();
        Assert.That(list.Count, Is.EqualTo(0));
    }

    [Test]
    public void Constructor_WithCapacity_InitialCountIsZeroAndIndices()
    {
        var list = new IndexedCircularList<string>(5);
        Assert.That(list.Count, Is.EqualTo(0));
        Assert.That(list.FirstIndex, Is.EqualTo(0));
        Assert.That(list.LastIndex, Is.EqualTo(-1));
    }

    [Test]
    public void Constructor_WithZeroCapacity_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => new IndexedCircularList<string>(0));
    }

    [Test]
    public void Constructor_WithNegativeCapacity_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => new IndexedCircularList<string>(-1));
    }

    [Test]
    public void Add_SingleItem_ReturnsZeroIndex()
    {
        var list = new IndexedCircularList<string>();
        var index = list.Add("A");
        Assert.That(index, Is.EqualTo(0));
    }

    [Test]
    public void Add_MultipleItems_ReturnsIncrementalIndices()
    {
        var list = new IndexedCircularList<string>();
        var idx1 = list.Add("A");
        var idx2 = list.Add("B");
        Assert.That(idx1, Is.EqualTo(0));
        Assert.That(idx2, Is.EqualTo(1));
    }

    [Test]
    public void Add_UnderCapacity_IncreasesCount()
    {
        var list = new IndexedCircularList<string>(3);
        list.Add("A");
        list.Add("B");
        Assert.That(list.Count, Is.EqualTo(2));
    }

    [Test]
    public void Add_AtCapacity_DoesNotIncreaseCountBeyondCapacity()
    {
        var list = new IndexedCircularList<string>(2);
        list.Add("A");
        list.Add("B");
        list.Add("C");
        Assert.That(list.Count, Is.EqualTo(2));
    }

    [Test]
    public void Count_AfterAdds_ReturnsCorrectCount()
    {
        var list = new IndexedCircularList<string>(4);
        list.Add("A");
        list.Add("B");
        list.Add("C");
        Assert.That(list.Count, Is.EqualTo(3));
    }

    [Test]
    public void FirstIndex_EmptyList_ReturnsZero()
    {
        var list = new IndexedCircularList<string>();
        Assert.That(list.FirstIndex, Is.EqualTo(0));
    }

    [Test]
    public void LastIndex_EmptyList_ReturnsMinusOne()
    {
        var list = new IndexedCircularList<string>();
        Assert.That(list.LastIndex, Is.EqualTo(-1));
    }

    [Test]
    public void FirstIndex_AfterAddsUnderCapacity()
    {
        var list = new IndexedCircularList<string>(5);
        list.Add("A");
        list.Add("B");
        Assert.That(list.FirstIndex, Is.EqualTo(0));
    }

    [Test]
    public void LastIndex_AfterAddsUnderCapacity()
    {
        var list = new IndexedCircularList<string>(5);
        list.Add("A");
        list.Add("B");
        Assert.That(list.LastIndex, Is.EqualTo(1));
    }

    [Test]
    public void FirstIndex_AfterAtCapacity()
    {
        var list = new IndexedCircularList<string>(3);
        list.Add("A");
        list.Add("B");
        list.Add("C");
        Assert.That(list.FirstIndex, Is.EqualTo(0));
    }

    [Test]
    public void LastIndex_AfterAtCapacity()
    {
        var list = new IndexedCircularList<string>(3);
        list.Add("A");
        list.Add("B");
        list.Add("C");
        Assert.That(list.LastIndex, Is.EqualTo(2));
    }

    [Test]
    public void FirstIndex_AfterOverCapacity()
    {
        var list = new IndexedCircularList<string>(3);
        list.Add("A");
        list.Add("B");
        list.Add("C");
        list.Add("D");
        Assert.That(list.FirstIndex, Is.EqualTo(1));
    }

    [Test]
    public void LastIndex_AfterOverCapacity()
    {
        var list = new IndexedCircularList<string>(3);
        list.Add("A");
        list.Add("B");
        list.Add("C");
        list.Add("D");
        Assert.That(list.LastIndex, Is.EqualTo(3));
    }

    [Test]
    public void Indexer_Get_ValidIndexUnderCapacity()
    {
        var list = new IndexedCircularList<string>(3);
        list.Add("A");
        list.Add("B");
        list.Add("C");
        Assert.That(list[1], Is.EqualTo("B"));
    }

    [Test]
    public void Indexer_Get_ValidIndexAtCapacity()
    {
        var list = new IndexedCircularList<string>(3);
        list.Add("A");
        list.Add("B");
        list.Add("C");
        Assert.That(list[2], Is.EqualTo("C"));
    }

    [Test]
    public void Indexer_Get_ValidIndexAfterWrap()
    {
        var list = new IndexedCircularList<string>(3);
        list.Add("A");
        list.Add("B");
        list.Add("C");
        list.Add("D");
        Assert.That(list[3], Is.EqualTo("D"));
        Assert.That(list[2], Is.EqualTo("C"));
    }

    [Test]
    public void Indexer_Get_IndexBelowFirst_ThrowsException()
    {
        var list = new IndexedCircularList<string>(3);
        list.Add("A");
        list.Add("B");
        list.Add("C");
        list.Add("D");
        Assert.Throws<IndexOutOfRangeException>(() =>
        {
            var _ = list[0];
        });
    }

    [Test]
    public void Indexer_Get_IndexAboveLast_ThrowsException()
    {
        var list = new IndexedCircularList<string>(3);
        list.Add("A");
        list.Add("B");
        Assert.Throws<IndexOutOfRangeException>(() =>
        {
            var _ = list[2];
        });
    }

    [Test]
    public void ValidateIndex_ValidIndex_ReturnsTrue()
    {
        var list = new IndexedCircularList<string>(3);
        list.Add("A");
        list.Add("B");
        Assert.That(list.ValidateIndex(1), Is.True);
    }

    [Test]
    public void ValidateIndex_IndexBelowFirst_ReturnsFalse()
    {
        var list = new IndexedCircularList<string>(3);
        list.Add("A");
        list.Add("B");
        Assert.That(list.ValidateIndex(-1), Is.False);
    }

    [Test]
    public void ValidateIndex_IndexAboveLast_ReturnsFalse()
    {
        var list = new IndexedCircularList<string>(3);
        list.Add("A");
        list.Add("B");
        Assert.That(list.ValidateIndex(2), Is.False);
    }

    [Test]
    public void TryGetValue_ValidIndex_ReturnsTrueAndValue()
    {
        var list = new IndexedCircularList<string>(3);
        list.Add("A");
        list.Add("B");
        string value;
        var result = list.TryGetValue(1, out value);
        Assert.That(result, Is.True);
        Assert.That(value, Is.EqualTo("B"));
    }

    [Test]
    public void TryGetValue_IndexBelowFirst_ReturnsFalseAndNull()
    {
        var list = new IndexedCircularList<string>(3);
        list.Add("A");
        string value;
        var result = list.TryGetValue(-1, out value);
        Assert.That(result, Is.False);
        Assert.That(value, Is.Null);
    }

    [Test]
    public void TryGetValue_IndexAboveLast_ReturnsFalseAndNull()
    {
        var list = new IndexedCircularList<string>(3);
        list.Add("A");
        string value;
        var result = list.TryGetValue(1, out value);
        Assert.That(result, Is.False);
        Assert.That(value, Is.Null);
    }

    [Test]
    public void Clear_OnNonEmpty_ListBecomesEmpty()
    {
        var list = new IndexedCircularList<string>(3);
        list.Add("A");
        list.Add("B");
        list.Clear();
        Assert.That(list.Count, Is.EqualTo(0));
        Assert.That(list.FirstIndex, Is.EqualTo(2));
        
        // One less than the FirstIndex to indicate a negative delta.
        Assert.That(list.LastIndex, Is.EqualTo(1));
    }

    [Test]
    public void Clear_ClearsBuffer()
    {
        var list = new IndexedCircularList<string>(3);
        list.Add("A");
        list.Add("B");
        list.Clear();
        string value;
        var result = list.TryGetValue(0, out value);
        Assert.That(result, Is.False);
        Assert.That(value, Is.Null);
    }

    [Test]
    public void Reset_DefaultThenAdd_SetsNextIndexToZero()
    {
        var list = new IndexedCircularList<string>(3);
        list.Add("A");
        list.Add("B");
        list.Reset();
        Assert.That(list.Count, Is.EqualTo(0));
        Assert.That(list.FirstIndex, Is.EqualTo(0));
        Assert.That(list.LastIndex, Is.EqualTo(-1));
        list.Add("C");
        Assert.That(list.LastIndex, Is.EqualTo(0));
    }

    [Test]
    public void Reset_WithCustomNextIndex_SetsNextIndex()
    {
        var list = new IndexedCircularList<string>(3);
        list.Add("A");
        list.Reset(5);
        Assert.That(list.Count, Is.EqualTo(0));
        Assert.That(list.FirstIndex, Is.EqualTo(5));
        Assert.That(list.LastIndex, Is.EqualTo(4));
        list.Add("B");
        Assert.That(list.LastIndex, Is.EqualTo(5));
        Assert.That(list[5], Is.EqualTo("B"));
    }

    [Test]
    public void Reset_ClearsBufferContents()
    {
        var list = new IndexedCircularList<string>(3);
        list.Add("A");
        list.Reset();
        Assert.Throws<IndexOutOfRangeException>(() =>
        {
            var _ = list[0];
        });
    }

    [Test]
    public void Enumeration_EmptyList_YieldsNoElements()
    {
        var list = new IndexedCircularList<string>(3);
        var items = list.ToList();
        Assert.That(items, Is.Empty);
    }

    [Test]
    public void Enumeration_UnderCapacity_YieldsItemsInOrder()
    {
        var list = new IndexedCircularList<string>(3);
        list.Add("A");
        list.Add("B");
        list.Add("C");
        var items = list.ToList();
        Assert.That(items.Select(t => t.Item), Is.EqualTo(new[] { "A", "B", "C" }));
        Assert.That(items.Select(t => t.Index), Is.EqualTo(new long[] { 0, 1, 2 }));
    }

    [Test]
    public void Enumeration_AfterWrap_YieldsItemsInOrder()
    {
        var list = new IndexedCircularList<string>(3);
        list.Add("A");
        list.Add("B");
        list.Add("C");
        list.Add("D");
        list.Add("E");
        var items = list.ToList();
        Assert.That(items.Select(t => t.Item), Is.EqualTo(new[] { "C", "D", "E" }));
        Assert.That(items.Select(t => t.Index), Is.EqualTo(new long[] { 2, 3, 4 }));
    }

    [Test]
    public void ConsecutiveOperations_AddClearAdd_CountAndIndicesCorrect()
    {
        var list = new IndexedCircularList<string>(3);
        list.Add("A");
        list.Add("B");
        list.Clear();
        list.Add("C");
        Assert.That(list.Count, Is.EqualTo(1));
        Assert.That(list.FirstIndex, Is.EqualTo(2));
        Assert.That(list.LastIndex, Is.EqualTo(2));
    }

    [Test]
    public void ConsecutiveOperations_AddResetAdd_CountAndIndicesCorrect()
    {
        var list = new IndexedCircularList<string>(3);
        list.Add("A");
        list.Add("B");
        list.Reset(10);
        list.Add("C");
        list.Add("D");
        Assert.That(list.FirstIndex, Is.EqualTo(10));
        Assert.That(list.LastIndex, Is.EqualTo(11));
    }

    [Test]
    public void Add_NullValueAllowed()
    {
        var list = new IndexedCircularList<string>(3);
        var idx = list.Add(null);
        Assert.That(idx, Is.EqualTo(0));
        Assert.That(list[0], Is.Null);
    }

    [Test]
    public void TryGetValue_NullStored_ReturnsTrueAndNull()
    {
        var list = new IndexedCircularList<string>(3);
        list.Add(null);
        string value;
        var result = list.TryGetValue(0, out value);
        Assert.That(result, Is.True);
        Assert.That(value, Is.Null);
    }

    [Test]
    public void Indexer_NullStored_ReturnsNull()
    {
        var list = new IndexedCircularList<string>(3);
        list.Add(null);
        Assert.That(list[0], Is.Null);
    }

    [Test]
    public void ValidateIndex_EmptyList_ReturnsFalse()
    {
        var list = new IndexedCircularList<string>(3);
        Assert.That(list.ValidateIndex(0), Is.False);
    }

    [Test]
    public void ValidateIndex_AfterWrap_CorrectTrueFalse()
    {
        var list = new IndexedCircularList<string>(3);
        list.Add("A");
        list.Add("B");
        list.Add("C");
        list.Add("D");
        Assert.That(list.ValidateIndex(0), Is.False);
        Assert.That(list.ValidateIndex(1), Is.True);
        Assert.That(list.ValidateIndex(2), Is.True);
        Assert.That(list.ValidateIndex(3), Is.True);
        Assert.That(list.ValidateIndex(4), Is.False);
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
    public void BufferModuloMappingIndexingBehavior()
    {
        var list = new IndexedCircularList<string>(3);
        list.Add("A");
        list.Add("B");
        list.Add("C");
        list.Add("D");
        bool ok = list.TryGetValue(3, out var val);
        Assert.That(ok, Is.True);
        Assert.That(val, Is.EqualTo("D"));
        Assert.That(list.ValidateIndex(0), Is.False);
    }

    [Test]
    public void ManyWraps_Enumeration_OnlyLastNItems()
    {
        var list = new IndexedCircularList<string>(3);
        for (int i = 0; i < 10; i++)
            list.Add($"X{i}");
        var items = list.ToList();
        Assert.That(items.Count, Is.EqualTo(3));
        Assert.That(items.Select(t => t.Index), Is.EqualTo(new long[] { 7, 8, 9 }));
    }

    [Test]
    public void FirstAndLastIndex_MonotonicBehavior()
    {
        var list = new IndexedCircularList<string>(3);
        for (int i = 0; i < 5; i++)
        {
            var idx = list.Add($"X{i}");
            Assert.That(list.LastIndex, Is.EqualTo(idx));
        }

        Assert.That(list.FirstIndex, Is.EqualTo(list.LastIndex - list.Count + 1));
    }

    [Test]
    public void FirstAndLastIndex_AfterResetThenAdd()
    {
        var list = new IndexedCircularList<string>(4);
        list.Add("A");
        list.Add("B");
        list.Reset(100);
        Assert.That(list.FirstIndex, Is.EqualTo(100));
        Assert.That(list.LastIndex, Is.EqualTo(99));
        list.Add("C");
        Assert.That(list.FirstIndex, Is.EqualTo(100));
        Assert.That(list.LastIndex, Is.EqualTo(100));
    }

    [Test]
    public void CapacityOne_WrapBehavior()
    {
        var list = new IndexedCircularList<string>(1);
        var idx1 = list.Add("A");
        var idx2 = list.Add("B");
        Assert.That(list.Count, Is.EqualTo(1));
        Assert.That(list.FirstIndex, Is.EqualTo(1));
        Assert.That(list.LastIndex, Is.EqualTo(1));
        Assert.That(list[1], Is.EqualTo("B"));
    }

    [Test]
    public void CapacityTwo_WrapTwice_Behavior()
    {
        var list = new IndexedCircularList<string>(2);
        list.Add("A");
        list.Add("B");
        list.Add("C");
        list.Add("D");
        var items = list.ToList();
        Assert.That(items.Select(t => t.Index), Is.EqualTo(new long[] { 2, 3 }));
        Assert.That(items.Select(t => t.Item), Is.EqualTo(new[] { "C", "D" }));
    }

    [Test]
    public void ValidateIndex_AfterClear_ReturnsFalse()
    {
        var list = new IndexedCircularList<string>(3);
        list.Add("A");
        list.Clear();
        Assert.That(list.ValidateIndex(0), Is.False);
    }

    [Test]
    public void TryGetValue_AfterReset_ReturnsFalseForOldIndex()
    {
        var list = new IndexedCircularList<string>(3);
        list.Add("A");
        list.Reset();
        Assert.That(list.TryGetValue(0, out _), Is.False);
    }
}
