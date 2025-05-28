using NexNet.Internals.Collections.Lists;
using NUnit.Framework;

namespace NexNet.IntegrationTests.Collections.Lists;

[TestFixture]
public class LockFreeArrayListTests
{
    [Test]
    public void Add_SingleItem_ListContainsItem()
    {
        var list = new LockFreeArrayList<int>();
        list.Add(42);
        Assert.That(list.Count(), Is.EqualTo(1));
        Assert.That(list.First(), Is.EqualTo(42));
    }

    [Test]
    public void Add_MultipleItems_YieldsReverseOrder()
    {
        var list = new LockFreeArrayList<int>();
        list.Add(1);
        list.Add(2);
        list.Add(3);
        var actual = list.ToArray();
        Assert.That(actual, Is.EqualTo(new[] { 3, 2, 1 }));
    }

    [Test]
    public void Add_Duplicates_EnumerationIncludesDuplicates()
    {
        var list = new LockFreeArrayList<int>();
        list.Add(5);
        list.Add(5);
        list.Add(5);
        Assert.That(list.Count(), Is.EqualTo(3));
        Assert.That(list, Is.EqualTo(new[] { 5, 5, 5 }));
    }

    [Test]
    public void Add_AfterRemove_ReinsertsCorrectly()
    {
        var list = new LockFreeArrayList<int>();
        list.Add(10);
        list.Remove(10);
        list.Add(20);
        Assert.That(list.Count(), Is.EqualTo(1));
        Assert.That(list.Single(), Is.EqualTo(20));
    }

    // ---- Insert tests ----

    [Test]
    public void Insert_AtHeadOnEmpty_WorksLikeAdd()
    {
        var list = new LockFreeArrayList<int>();
        list.Insert(0, 99);
        Assert.That(list.ToArray(), Is.EqualTo(new[] { 99 }));
    }

    [Test]
    public void Insert_AtHeadOnNonEmpty_ShiftsOthers()
    {
        var list = new LockFreeArrayList<int>();
        list.Add(1);
        list.Add(2); // list: 2,1
        list.Insert(0, 3); // list: 3,2,1
        Assert.That(list.ToArray(), Is.EqualTo(new[] { 3, 2, 1 }));
    }

    [Test]
    public void Insert_AtTail_Appends()
    {
        var list = new LockFreeArrayList<int>();
        list.Add(1);
        list.Add(2); // 2,1
        list.Insert(2, 3); // index==Count => append => 2,1,3
        Assert.That(list.ToArray(), Is.EqualTo(new[] { 2, 1, 3 }));
    }

    [Test]
    public void Insert_InMiddle_BetweenNodes()
    {
        var list = new LockFreeArrayList<int>();
        list.Add(1);
        list.Add(2);
        list.Add(3); // 3,2,1
        list.Insert(1, 4); // 3,4,2,1
        Assert.That(list.ToArray(), Is.EqualTo(new[] { 3, 4, 2, 1 }));
    }

    [Test]
    public void Insert_NegativeIndex_ThrowsArgumentOutOfRange()
    {
        var list = new LockFreeArrayList<int>();
        Assert.That(() => list.Insert(-1, 5),
            Throws.TypeOf<ArgumentOutOfRangeException>());
    }

    [Test]
    public void Insert_IndexGreaterThanCount_ThrowsArgumentOutOfRange()
    {
        var list = new LockFreeArrayList<int>();
        list.Add(1);
        // Count==1 so index>1 invalid
        Assert.That(() => list.Insert(2, 7),
            Throws.TypeOf<ArgumentOutOfRangeException>());
    }

    [Test]
    public void Insert_ThenAdd_ProducesExpectedSequence()
    {
        var list = new LockFreeArrayList<int>();
        list.Insert(0, 100); // [100]
        list.Add(200); // [200,100]
        list.Insert(1, 150); // [200,150,100]
        Assert.That(list.ToArray(), Is.EqualTo(new[] { 200, 150, 100 }));
    }

    [Test]
    public void Add_ThenInsertInMiddle_ProducesExpectedSequence()
    {
        var list = new LockFreeArrayList<int>();
        list.Add(1);
        list.Add(3); // [3,1]
        list.Insert(1, 2); // [3,2,1]
        Assert.That(list.ToArray(), Is.EqualTo(new[] { 3, 2, 1 }));
    }

    // ---- Remove tests ----

    [Test]
    public void Remove_ExistingItem_ReturnsTrueAndRemoves()
    {
        var list = new LockFreeArrayList<int>();
        list.Add(1);
        list.Add(2);
        Assert.That(list.Remove(1), Is.True);
        Assert.That(list.Contains(1), Is.False);
        Assert.That(list.Count(), Is.EqualTo(1));
    }

    [Test]
    public void Remove_NonExistingItem_ReturnsFalse()
    {
        var list = new LockFreeArrayList<int>();
        list.Add(1);
        Assert.That(list.Remove(99), Is.False);
        Assert.That(list.Count(), Is.EqualTo(1));
    }

    [Test]
    public void Remove_OnEmptyList_ReturnsFalse()
    {
        var list = new LockFreeArrayList<int>();
        Assert.That(list.Remove(5), Is.False);
    }

    [Test]
    public void Remove_DuplicateItems_RemovesOnlyFirstOccurrence()
    {
        var list = new LockFreeArrayList<int>();
        list.Add(1);
        list.Add(2);
        list.Add(1);
        // initial: [1,2,1]
        Assert.That(list.Remove(1), Is.True);
        // now should remove the head 1 => [2,1]
        Assert.That(list.ToArray(), Is.EqualTo(new[] { 2, 1 }));
    }

    [Test]
    public void Remove_HeadItem_FixesHead()
    {
        var list = new LockFreeArrayList<int>();
        list.Add(10);
        list.Add(20); // [20,10]
        Assert.That(list.Remove(20), Is.True);
        Assert.That(list.ToArray(), Is.EqualTo(new[] { 10 }));
    }

    [Test]
    public void Remove_TailItem_FixesTail()
    {
        var list = new LockFreeArrayList<int>();
        list.Add(10);
        list.Add(20); // [20,10]
        Assert.That(list.Remove(10), Is.True);
        Assert.That(list.ToArray(), Is.EqualTo(new[] { 20 }));
    }

    [Test]
    public void Remove_NullReferenceType_Works()
    {
        var list = new LockFreeArrayList<string>();
        list.Add(null);
        list.Add("foo");
        Assert.That(list.Remove(null), Is.True);
        Assert.That(list.Contains(null), Is.False);
        Assert.That(list, Is.EqualTo(new[] { "foo" }));
    }

    [Test]
    public void Remove_NullNotPresent_ReturnsFalse()
    {
        var list = new LockFreeArrayList<string>();
        list.Add("bar");
        Assert.That(list.Remove(null), Is.False);
    }

    // ---- Enumeration tests ----

    [Test]
    public void Enumeration_GenericEnumerator_ReturnsCorrectSequence()
    {
        var list = new LockFreeArrayList<int>();
        for (int i = 0; i < 5; i++) list.Add(i + 1); // [5,4,3,2,1]
        var seq = list.ToArray();
        Assert.That(seq, Is.EqualTo(new[] { 5, 4, 3, 2, 1 }));
    }
    
    [Test]
    public void Enumeration_OnEmptyList_YieldsNothing()
    {
        var list = new LockFreeArrayList<int>();
        Assert.That(list.Any(), Is.False);
    }

    [Test]
    public void Enumeration_SkipsRemovedItems()
    {
        var list = new LockFreeArrayList<int>();
        list.Add(1);
        list.Add(2);
        list.Add(3); // [3,2,1]
        list.Remove(2);
        Assert.That(list.ToArray(), Is.EqualTo(new[] { 3, 1 }));
    }

    [Test]
    public void Enumeration_Snapshot_IsolatedFromConcurrentAdd()
    {
        var list = new LockFreeArrayList<int>();
        list.Add(1);
        list.Add(2);
        var enumerator = list.GetEnumerator();
        list.Add(3);
        // snapshot should not include the 3
        var captured = new List<int>();
        while (enumerator.MoveNext())
            captured.Add(enumerator.Current);
        Assert.That(captured, Is.EqualTo(new[] { 2, 1 }));
    }

    [Test]
    public void MultipleEnumerations_ProduceSameSequence()
    {
        var list = new LockFreeArrayList<int>();
        list.Add(1);
        list.Add(2);
        var first = list.ToArray();
        var second = list.ToArray();
        Assert.That(second, Is.EqualTo(first));
    }

    [Test]
    public void Enumerator_Reset_ThrowsNotSupported()
    {
        var list = new LockFreeArrayList<int>();
        list.Add(9);
        var ie = list.GetEnumerator();
        Assert.That(() => ie.Reset(), Throws.TypeOf<NotSupportedException>());
    }

    // ---- Generic/value/reference type tests ----

    [Test]
    public void WorksWithReferenceType_String()
    {
        var list = new LockFreeArrayList<string>();
        list.Add("a");
        list.Add("b");
        Assert.That(list.ToArray(), Is.EqualTo(new[] { "b", "a" }));
    }

    [Test]
    public void WorksWithValueType_Int()
    {
        var list = new LockFreeArrayList<int>();
        list.Add(3);
        Assert.That(list.Single(), Is.EqualTo(3));
    }

    private struct Point
    {
        public int X, Y;
    }

    [Test]
    public void WorksWithStructType_Point()
    {
        var list = new LockFreeArrayList<Point>();
        list.Add(new Point { X = 1, Y = 2 });
        var pt = list.Single();
        Assert.That((pt.X, pt.Y), Is.EqualTo((1, 2)));
    }

    [Test]
    public void WorksWithNullableValueType_IntNullable()
    {
        var list = new LockFreeArrayList<int?>();
        list.Add(null);
        list.Add(5);
        Assert.That(list.ToArray(), Is.EqualTo(new int?[] { 5, null }));
    }

    // ---- Capacity / growth tests ----

    [Test]
    public void GrowArray_WhenExceedInitialCapacity_NoErrors()
    {
        var list = new LockFreeArrayList<int>(1);
        for (int i = 0; i < 20; i++) list.Add(i);
        Assert.That(list.Count(), Is.EqualTo(20));
    }

    [Test]
    public void AddHundredItems_CapacityGrowsSilently()
    {
        var list = new LockFreeArrayList<int>();
        for (int i = 0; i < 100; i++) list.Add(i);
        Assert.That(list.Count(), Is.EqualTo(100));
    }

    [Test]
    public void RemoveAfterGrowth_MaintainsCorrectSequence()
    {
        var list = new LockFreeArrayList<int>();
        for (int i = 0; i < 20; i++) list.Add(i);
        list.Remove(10);
        Assert.That(list.Contains(10), Is.False);
        Assert.That(list.Count(), Is.EqualTo(19));
    }

    // ---- Constructor argument tests ----

    [Test]
    public void Constructor_NegativeCapacity_Throws()
    {
        Assert.That(() => new LockFreeArrayList<int>(-5),
            Throws.TypeOf<ArgumentOutOfRangeException>());
    }

    [Test]
    public void Constructor_ZeroCapacity_ThrowsIndexOutOfRange()
    {
        Assert.That(() => new LockFreeArrayList<int>(0),
            Throws.TypeOf<IndexOutOfRangeException>());
    }

    [Test]
    public void Constructor_PositiveCapacity_NoThrow()
    {
        Assert.That(() => new LockFreeArrayList<int>(5), Throws.Nothing);
    }

    [Test]
    public void Linq_CountExtension_Matches()
    {
        var list = new LockFreeArrayList<int>();
        list.Add(1);
        list.Add(2);
        Assert.That(list.Count(), Is.EqualTo(2));
    }

    [Test]
    public void Linq_FirstExtension_ReturnsHead()
    {
        var list = new LockFreeArrayList<int>();
        list.Add(10);
        list.Add(20); // [20,10]
        Assert.That(list.First(), Is.EqualTo(20));
    }

    [Test]
    public void Linq_LastExtension_ReturnsTail()
    {
        var list = new LockFreeArrayList<int>();
        list.Add(5);
        list.Add(6); // [6,5]
        Assert.That(list.Last(), Is.EqualTo(5));
    }

    [Test]
    public void Linq_WhereExtension_FiltersCorrectly()
    {
        var list = new LockFreeArrayList<int>();
        list.Add(1);
        list.Add(2);
        list.Add(3);
        var evens = list.Where(x => x % 2 == 0).ToArray();
        Assert.That(evens, Is.EqualTo(new[] { 2 }));
    }

    // ---- Custom equality behavior tests ----

    private class Person
    {
        public string Name;

        public override bool Equals(object obj)
            => obj is Person p && p.Name == Name;

        public override int GetHashCode() => Name?.GetHashCode() ?? 0;
    }

    [Test]
    public void Remove_UsesDefaultEqualityComparer_ForCustomClass()
    {
        var list = new LockFreeArrayList<Person>();
        var p1 = new Person { Name = "Alice" };
        var p2 = new Person { Name = "Alice" };
        list.Add(p1);
        // p2 is equal by value, so Remove(p2) should succeed
        Assert.That(list.Remove(p2), Is.True);
        Assert.That(list.Any(), Is.False);
    }

    [Test]
    public void Remove_ReferenceTypeWithoutOverride_RespectsReferenceEquality()
    {
        var list = new LockFreeArrayList<object>();
        var o1 = new object();
        var o2 = new object();
        list.Add(o1);
        Assert.That(list.Remove(o2), Is.False);
        Assert.That(list.Remove(o1), Is.True);
    }

    // ---- Concurrency tests ----

    [Test]
    public void ConcurrentAdd_TenThreads_AllItemsPresent()
    {
        var list = new LockFreeArrayList<int>();
        var threads = new List<Thread>();
        for (int i = 0; i < 10; i++)
        {
            int v = i;
            threads.Add(new Thread(() => list.Add(v)));
        }

        threads.ForEach(t => t.Start());
        threads.ForEach(t => t.Join());
        var result = list.ToArray();
        Assert.That(result.Length, Is.EqualTo(10));
        Assert.That(result, Is.EquivalentTo(Enumerable.Range(0, 10)));
    }

    [Test]
    public void ConcurrentAdd_HundredThreads_AllItemsPresent()
    {
        var list = new LockFreeArrayList<int>();
        var threads = new List<Thread>();
        for (int i = 0; i < 100; i++)
        {
            int v = i;
            threads.Add(new Thread(() => list.Add(v)));
        }

        threads.ForEach(t => t.Start());
        threads.ForEach(t => t.Join());
        var result = list.ToArray();
        Assert.That(result.Length, Is.EqualTo(100));
        Assert.That(result, Is.EquivalentTo(Enumerable.Range(0, 100)));
    }

    [Test]
    public void ConcurrentRemove_FiftyThreads_AllItemsRemoved()
    {
        var list = new LockFreeArrayList<int>();
        for (int i = 0; i < 50; i++) list.Add(i);
        var threads = new List<Thread>();
        for (int i = 0; i < 50; i++)
        {
            int v = i;
            threads.Add(new Thread(() => Assert.That(list.Remove(v), Is.True)));
        }

        threads.ForEach(t => t.Start());
        threads.ForEach(t => t.Join());
        Assert.That(list.Count(), Is.EqualTo(0));
    }

    [Test]
    public void Contains_PresentValue_ReturnsTrue()
    {
        var list = new LockFreeArrayList<int>();
        list.Add(1);
        Assert.That(list.Contains(1), Is.True);
    }

    [Test]
    public void Contains_AbsentValue_ReturnsFalse()
    {
        var list = new LockFreeArrayList<int>();
        list.Add(2);
        Assert.That(list.Contains(1), Is.False);
    }

    [Test]
    public void Contains_AfterAddAndRemove_Correct()
    {
        var list = new LockFreeArrayList<int>();
        list.Add(5);
        list.Remove(5);
        Assert.That(list.Contains(5), Is.False);
    }

    [Test]
    public void Count_EmptyList_IsZero()
    {
        var list = new LockFreeArrayList<int>();
        Assert.That(list.Count(), Is.Zero);
    }

    [Test]
    public void Count_AfterInsertAndRemove_IsCorrect()
    {
        var list = new LockFreeArrayList<int>();
        list.Insert(0, 1);
        list.Insert(1, 2);
        list.Remove(1);
        Assert.That(list.Count(), Is.EqualTo(1));
    }

    [Test]
    public void ToArray_EmptyList_ReturnsEmptyArray()
    {
        var list = new LockFreeArrayList<int>();
        Assert.That(list.ToArray(), Is.Empty);
    }

    [Test]
    public void ToArray_AfterOperations_CorrectSequence()
    {
        var list = new LockFreeArrayList<int>();
        list.Add(1);
        list.Insert(1, 2);
        list.Add(3);
        list.Remove(1);
        // operations: Add(1)->[1], Insert(1,2)->[1,2], Add(3)->[3,1,2], Remove(1)->[3,2]
        Assert.That(list.ToArray(), Is.EqualTo(new[] { 3, 2 }));
    }

    [Test]
    public void RemoveAllOccurrences_Iteratively_RemovesAll()
    {
        var list = new LockFreeArrayList<int>();
        list.Add(1);
        list.Add(1);
        list.Add(1);
        while (list.Remove(1)) { }

        Assert.That(list.Any(), Is.False);
    }

    [Test]
    public void ReuseSlots_AfterRemove_AddDoesNotGrow()
    {
        var list = new LockFreeArrayList<int>(4);
        // fill to capacity
        for (int i = 0; i < 4; i++) list.Add(i);
        // remove all
        for (int i = 0; i < 4; i++) list.Remove(i);
        // re-add four new items
        for (int i = 10; i < 14; i++) list.Add(i);
        // expect head-first: [13,12,11,10]
        Assert.That(list.ToArray(), Is.EqualTo(new[] { 13, 12, 11, 10 }));
    }

    [Test]
    public void Stress_AddRemoveCycles_NoExceptions()
    {
        var list = new LockFreeArrayList<int>();
        for (int cycle = 0; cycle < 10; cycle++)
        {
            list.Add(cycle);
            Assert.That(() => list.Remove(cycle), Throws.Nothing);
        }

        Assert.That(list.Any(), Is.False);
    }

    [Test]
    public void EnumerationSnapshot_AfterRemove_Isolated()
    {
        var list = new LockFreeArrayList<int>();
        list.Add(1);
        list.Add(2);
        var enumerator = list.GetEnumerator();
        list.Remove(2);
        var captured = new List<int>();
        while (enumerator.MoveNext()) captured.Add(enumerator.Current);
        // snapshot: [2,1]
        Assert.That(captured, Is.EqualTo(new[] { 2, 1 }));
    }

    [Test]
    public void EnumerationSnapshot_MultipleEnumeratorsIndependent()
    {
        var list = new LockFreeArrayList<int>();
        list.Add(1);
        list.Add(2);
        var e1 = list.GetEnumerator();
        var e2 = list.GetEnumerator();
        list.Add(3);
        var seq1 = new List<int>();
        var seq2 = new List<int>();
        while (e1.MoveNext()) seq1.Add(e1.Current);
        while (e2.MoveNext()) seq2.Add(e2.Current);
        // Both should see only [2,1]
        Assert.That(seq1, Is.EqualTo(new[] { 2, 1 }));
        Assert.That(seq2, Is.EqualTo(new[] { 2, 1 }));
    }

    [Test]
    public void GenericEnumerator_Dispose_DoesNotThrow()
    {
        var list = new LockFreeArrayList<int>();
        list.Add(1);
        var en = list.GetEnumerator();
        Assert.That(() => en.Dispose(), Throws.Nothing);
    }
    

    [Test]
    public void IEnumerableGetEnumerator_ReturnsDifferentInstances()
    {
        var list = new LockFreeArrayList<int>();
        list.Add(1);
        var e1 = list.GetEnumerator();
        var e2 = list.GetEnumerator();
        Assert.That(e1, Is.Not.SameAs(e2));
    }

    [Test]
    public void Insert_NullReferenceTypeAtIndex_Works()
    {
        var list = new LockFreeArrayList<string>();
        list.Add("a");
        list.Insert(1, null);
        Assert.That(list.ToArray(), Is.EqualTo(new string[] { "a", null }));
    }

    [Test]
    public void Insert_ConsecutiveAtSameIndex_PreservesOrder()
    {
        var list = new LockFreeArrayList<string>();
        list.Add("C");
        list.Add("B");
        list.Add("A"); // [A,B,C]
        list.Insert(1, "D"); // [A,D,B,C]
        list.Insert(1, "E"); // [A,E,D,B,C]
        Assert.That(list.ToArray(), Is.EqualTo(new[] { "A", "E", "D", "B", "C" }));
    }

    [Test]
    public void Insert_AtTailAfterRemovals_Appends()
    {
        var list = new LockFreeArrayList<int>();
        list.Add(1);
        list.Add(2); // [2,1]
        list.Remove(1); // [2]
        list.Insert(1, 3); // append => [2,3]
        Assert.That(list.ToArray(), Is.EqualTo(new[] { 2, 3 }));
    }

    [Test]
    public void MixedAddInsertRemove_SequenceCorrect()
    {
        var list = new LockFreeArrayList<int>();
        list.Add(10); // [10]
        list.Insert(1, 20); // [10,20]
        list.Add(30); // [30,10,20]
        list.Remove(10); // [30,20]
        Assert.That(list.ToArray(), Is.EqualTo(new[] { 30, 20 }));
    }

    [Test]
    public void MixedOperations_CountCorrect()
    {
        var list = new LockFreeArrayList<int>();
        list.Add(10);
        list.Insert(1, 20);
        list.Add(30);
        list.Remove(10);
        Assert.That(list.Count(), Is.EqualTo(2));
    }

    [Test]
    public void Remove_DefaultValueTypeNotPresent_ReturnsFalse()
    {
        var list = new LockFreeArrayList<int>();
        Assert.That(list.Remove(default(int)), Is.False);
    }

    [Test]
    public void Remove_DefaultValueTypePresent_ReturnsTrue()
    {
        var list = new LockFreeArrayList<int>();
        list.Add(default(int));
        Assert.That(list.Remove(default(int)), Is.True);
    }

    [Test]
    public void Add_OneThousandItems_AllPresent()
    {
        var list = new LockFreeArrayList<int>();
        for (int i = 0; i < 1000; i++) list.Add(i);
        Assert.That(list.Count(), Is.EqualTo(1000));
        // spot-check a few
        Assert.That(list.Contains(0), Is.True);
        Assert.That(list.Contains(999), Is.True);
    }

    [Test]
    public void Remove_HalfItems_AllRemoved()
    {
        var list = new LockFreeArrayList<int>();
        for (int i = 0; i < 10; i++) list.Add(i); // [9..0]
        for (int i = 0; i < 10; i += 2) list.Remove(i); // remove evens
        Assert.That(list.ToArray(), Is.EqualTo(new[] { 9, 7, 5, 3, 1 }));
    }

    [Test]
    public void ConcurrentAddRemoveForEachValue_NoRemainingItems()
    {
        var list = new LockFreeArrayList<int>();
        var threads = new List<Thread>();
        for (int i = 0; i < 50; i++)
        {
            int v = i;
            threads.Add(new Thread(() =>
            {
                list.Add(v);
                list.Remove(v);
            }));
        }

        threads.ForEach(t => t.Start());
        threads.ForEach(t => t.Join());
        Assert.That(list.Any(), Is.False);
    }

    [Test]
    public void Linq_AnyExtension_ReturnsTrueWhenNotEmpty()
    {
        var list = new LockFreeArrayList<int>();
        list.Add(1);
        Assert.That(list.Any(), Is.True);
    }

    [Test]
    public void Linq_AnyExtension_ReturnsFalseWhenEmpty()
    {
        var list = new LockFreeArrayList<int>();
        Assert.That(list.Any(), Is.False);
    }

    [Test]
    public void Linq_ContainsExtension_ReturnsTrue()
    {
        var list = new LockFreeArrayList<int>();
        list.Add(5);
        Assert.That(list.Contains(5), Is.True);
    }

    [Test]
    public void Linq_ContainsExtension_ReturnsFalse()
    {
        var list = new LockFreeArrayList<int>();
        Assert.That(list.Contains(5), Is.False);
    }

    [Test]
    public void Linq_ElementAt_IndexValid_ReturnsCorrect()
    {
        var list = new LockFreeArrayList<int>();
        list.Add(1);
        list.Add(2); // [2,1]
        Assert.That(list.ElementAt(0), Is.EqualTo(2));
    }

    [Test]
    public void Linq_ElementAt_IndexInvalid_Throws()
    {
        var list = new LockFreeArrayList<int>();
        Assert.That(() => list.ElementAt(0), Throws.TypeOf<ArgumentOutOfRangeException>());
    }

    [Test]
    public void Linq_SkipTake_WorkingOnList()
    {
        var list = new LockFreeArrayList<int>();
        for (int i = 1; i <= 5; i++) list.Add(i); // [5,4,3,2,1]
        var subset = list.Skip(1).Take(3).ToArray(); // [4,3,2]
        Assert.That(subset, Is.EqualTo(new[] { 4, 3, 2 }));
    }

    [Test]
    public void Linq_Aggregate_CorrectSum()
    {
        var list = new LockFreeArrayList<int>();
        list.Add(1);
        list.Add(2);
        list.Add(3); // [3,2,1]
        var sum = list.Aggregate((a, b) => a + b);
        Assert.That(sum, Is.EqualTo(6));
    }

    [Test]
    public void Linq_ToList_Works()
    {
        var list = new LockFreeArrayList<int>();
        list.Add(1);
        list.Add(2);
        var asList = list.ToList();
        Assert.That(asList, Is.EqualTo(list.ToArray()));
    }

    [Test]
    public void Linq_Max_Works()
    {
        var list = new LockFreeArrayList<int>();
        list.Add(1);
        list.Add(3);
        list.Add(2);
        Assert.That(list.Max(), Is.EqualTo(3));
    }

    [Test]
    public void Linq_Min_Works()
    {
        var list = new LockFreeArrayList<int>();
        list.Add(1);
        list.Add(3);
        list.Add(2);
        Assert.That(list.Min(), Is.EqualTo(1));
    }

    [Test]
    public void Linq_OrderBy_Works()
    {
        var list = new LockFreeArrayList<int>();
        list.Add(2);
        list.Add(3);
        list.Add(1); // [1,3,2]
        var sorted = list.OrderBy(x => x).ToArray();
        Assert.That(sorted, Is.EqualTo(new[] { 1, 2, 3 }));
    }

    [Test]
    public void Linq_Distinct_Works()
    {
        var list = new LockFreeArrayList<int>();
        list.Add(1);
        list.Add(2);
        list.Add(2);
        list.Add(3); // [3,2,2,1]
        var distinct = list.Distinct().ToArray();
        Assert.That(distinct, Is.EqualTo(new[] { 3, 2, 1 }));
    }

    [Test]
    public void Linq_Reverse_Works()
    {
        var list = new LockFreeArrayList<int>();
        list.Add(1);
        list.Add(2);
        list.Add(3); // [3,2,1]
        var rev = list.Reverse().ToArray();
        Assert.That(rev, Is.EqualTo(new[] { 1, 2, 3 }));
    }

    [Test]
    public void Linq_All_Works()
    {
        var list = new LockFreeArrayList<int>();
        list.Add(2);
        list.Add(4);
        list.Add(6);
        Assert.That(list.All(x => x % 2 == 0), Is.True);
    }

    [Test]
    public void Linq_AnyWithPredicate_Works()
    {
        var list = new LockFreeArrayList<int>();
        list.Add(1);
        list.Add(2);
        list.Add(3);
        Assert.That(list.Any(x => x == 2), Is.True);
    }

    [Test]
    public void Linq_AnyWithPredicate_NotFound_Works()
    {
        var list = new LockFreeArrayList<int>();
        list.Add(1);
        list.Add(3);
        list.Add(5);
        Assert.That(list.Any(x => x == 2), Is.False);
    }

    [Test]
    public void Linq_CountWithPredicate_Works()
    {
        var list = new LockFreeArrayList<int>();
        list.Add(1);
        list.Add(2);
        list.Add(2);
        list.Add(3);
        Assert.That(list.Count(x => x == 2), Is.EqualTo(2));
    }

    [Test]
    public void Linq_SumExtension_Works()
    {
        var list = new LockFreeArrayList<int>();
        list.Add(1);
        list.Add(2);
        list.Add(3);
        Assert.That(list.Sum(), Is.EqualTo(6));
    }

    [Test]
    public void Linq_Except_Works()
    {
        var list = new LockFreeArrayList<int>();
        list.Add(1);
        list.Add(2);
        list.Add(3); // [3,2,1]
        var diff = list.Except(new[] { 2 }).ToArray();
        Assert.That(diff, Is.EqualTo(new[] { 3, 1 }));
    }

    [Test]
    public void Linq_Intersect_Works()
    {
        var list = new LockFreeArrayList<int>();
        list.Add(1);
        list.Add(2);
        list.Add(3); // [3,2,1]
        var inter = list.Intersect(new[] { 2, 3 }).ToArray();
        Assert.That(inter, Is.EqualTo(new[] { 3, 2 }));
    }

    [Test]
    public void Linq_Union_Works()
    {
        var list = new LockFreeArrayList<int>();
        list.Add(1);
        list.Add(2); // [2,1]
        var uni = list.Union(new[] { 2, 3 }).ToArray();
        Assert.That(uni, Is.EqualTo(new[] { 2, 1, 3 }));
    }

    [Test]
    public void Linq_Concat_Works()
    {
        var list = new LockFreeArrayList<int>();
        list.Add(1);
        list.Add(2);
        list.Add(3); // [3,2,1]
        var con = list.Concat(new[] { 4, 5 }).ToArray();
        Assert.That(con, Is.EqualTo(new[] { 3, 2, 1, 4, 5 }));
    }

    [Test]
    public void Remove_AfterRemove_ReturnsFalseSecondTime()
    {
        var list = new LockFreeArrayList<int>();
        list.Add(1);
        Assert.That(list.Remove(1), Is.True);
        Assert.That(list.Remove(1), Is.False);
    }

    [Test]
    public void InsertAtBeginning_MultipleTimes_PreservesSequence()
    {
        var list = new LockFreeArrayList<int>();
        for (int i = 0; i < 10; i++)
            list.Insert(0, i);
        Assert.That(
            list.ToArray(),
            Is.EqualTo(new[] { 9, 8, 7, 6, 5, 4, 3, 2, 1, 0 })
        );
    }
}
