using NexNet.Internals.Collections.Lists;
using System.Threading.Tasks;

namespace NexNet.IntegrationTests.Collections.Lists;
public class SnapshotListTests
{
    [Test]
    public async Task Add_SingleItem_ListContainsItem()
    {
        var list = new SnapshotList<int>();
        list.Add(42);
        await Assert.That(list.Count()).IsEqualTo(1);
        await Assert.That(list.First()).IsEqualTo(42);
    }

    [Test]
    public async Task Add_MultipleItems_YieldsReverseOrder()
    {
        var list = new SnapshotList<int>();
        list.Add(1);
        list.Add(2);
        list.Add(3);
        var actual = list.ToArray();
        await Assert.That(actual).IsEqualTo(new[] { 3, 2, 1 });
    }

    [Test]
    public async Task Add_Duplicates_EnumerationIncludesDuplicates()
    {
        var list = new SnapshotList<int>();
        list.Add(5);
        list.Add(5);
        list.Add(5);
        await Assert.That(list.Count()).IsEqualTo(3);
        await Assert.That(list).IsEqualTo(new[] { 5, 5, 5 });
    }

    [Test]
    public async Task Add_AfterRemove_ReinsertsCorrectly()
    {
        var list = new SnapshotList<int>();
        list.Add(10);
        list.Remove(10);
        list.Add(20);
        await Assert.That(list.Count()).IsEqualTo(1);
        await Assert.That(list.Single()).IsEqualTo(20);
    }

    // ---- Insert tests ----

    [Test]
    public async Task Insert_AtHeadOnEmpty_WorksLikeAdd()
    {
        var list = new SnapshotList<int>();
        list.Insert(0, 99);
        await Assert.That(list.ToArray()).IsEqualTo(new[] { 99 });
    }

    [Test]
    public async Task Insert_AtHeadOnNonEmpty_ShiftsOthers()
    {
        var list = new SnapshotList<int>();
        list.Add(1);
        list.Add(2); // list: 2,1
        list.Insert(0, 3); // list: 3,2,1
        await Assert.That(list.ToArray()).IsEqualTo(new[] { 3, 2, 1 });
    }

    [Test]
    public async Task Insert_AtTail_Appends()
    {
        var list = new SnapshotList<int>();
        list.Add(1);
        list.Add(2); // 2,1
        list.Insert(2, 3); // index==Count => append => 2,1,3
        await Assert.That(list.ToArray()).IsEqualTo(new[] { 2, 1, 3 });
    }

    [Test]
    public async Task Insert_InMiddle_BetweenNodes()
    {
        var list = new SnapshotList<int>();
        list.Add(1);
        list.Add(2);
        list.Add(3); // 3,2,1
        list.Insert(1, 4); // 3,4,2,1
        await Assert.That(list.ToArray()).IsEqualTo(new[] { 3, 4, 2, 1 });
    }

    [Test]
    public async Task Insert_NegativeIndex_ThrowsArgumentOutOfRange()
    {
        var list = new SnapshotList<int>();
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => list.Insert(-1, 5));
    }

    [Test]
    public async Task Insert_IndexGreaterThanCount_ThrowsArgumentOutOfRange()
    {
        var list = new SnapshotList<int>();
        list.Add(1);
        // Count==1 so index>1 invalid
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => list.Insert(2, 7));
    }

    [Test]
    public async Task Insert_ThenAdd_ProducesExpectedSequence()
    {
        var list = new SnapshotList<int>();
        list.Insert(0, 100); // [100]
        list.Add(200); // [200,100]
        list.Insert(1, 150); // [200,150,100]
        await Assert.That(list.ToArray()).IsEqualTo(new[] { 200, 150, 100 });
    }

    [Test]
    public async Task Add_ThenInsertInMiddle_ProducesExpectedSequence()
    {
        var list = new SnapshotList<int>();
        list.Add(1);
        list.Add(3); // [3,1]
        list.Insert(1, 2); // [3,2,1]
        await Assert.That(list.ToArray()).IsEqualTo(new[] { 3, 2, 1 });
    }

    // ---- Remove tests ----

    [Test]
    public async Task Remove_ExistingItem_ReturnsTrueAndRemoves()
    {
        var list = new SnapshotList<int>();
        list.Add(1);
        list.Add(2);
        await Assert.That(list.Remove(1)).IsTrue();
        await Assert.That(list.Contains(1)).IsFalse();
        await Assert.That(list.Count()).IsEqualTo(1);
    }

    [Test]
    public async Task Remove_NonExistingItem_ReturnsFalse()
    {
        var list = new SnapshotList<int>();
        list.Add(1);
        await Assert.That(list.Remove(99)).IsFalse();
        await Assert.That(list.Count()).IsEqualTo(1);
    }

    [Test]
    public async Task Remove_OnEmptyList_ReturnsFalse()
    {
        var list = new SnapshotList<int>();
        await Assert.That(list.Remove(5)).IsFalse();
    }

    [Test]
    public async Task Remove_DuplicateItems_RemovesOnlyFirstOccurrence()
    {
        var list = new SnapshotList<int>();
        list.Add(1);
        list.Add(2);
        list.Add(1);
        // initial: [1,2,1]
        await Assert.That(list.Remove(1)).IsTrue();
        // now should remove the head 1 => [2,1]
        await Assert.That(list.ToArray()).IsEqualTo(new[] { 2, 1 });
    }

    [Test]
    public async Task Remove_HeadItem_FixesHead()
    {
        var list = new SnapshotList<int>();
        list.Add(10);
        list.Add(20); // [20,10]
        await Assert.That(list.Remove(20)).IsTrue();
        await Assert.That(list.ToArray()).IsEqualTo(new[] { 10 });
    }

    [Test]
    public async Task Remove_TailItem_FixesTail()
    {
        var list = new SnapshotList<int>();
        list.Add(10);
        list.Add(20); // [20,10]
        await Assert.That(list.Remove(10)).IsTrue();
        await Assert.That(list.ToArray()).IsEqualTo(new[] { 20 });
    }

    [Test]
    public async Task Remove_NullReferenceType_Works()
    {
        var list = new SnapshotList<string?>();
        list.Add(null);
        list.Add("foo");
        await Assert.That(list.Remove(null)).IsTrue();
        await Assert.That(list.Contains(null)).IsFalse();
        await Assert.That(list).IsEqualTo(new[] { "foo" });
    }

    [Test]
    public async Task Remove_NullNotPresent_ReturnsFalse()
    {
        var list = new SnapshotList<string?>();
        list.Add("bar");
        await Assert.That(list.Remove(null)).IsFalse();
    }

    // ---- Enumeration tests ----

    [Test]
    public async Task Enumeration_GenericEnumerator_ReturnsCorrectSequence()
    {
        var list = new SnapshotList<int>();
        for (int i = 0; i < 5; i++) list.Add(i + 1); // [5,4,3,2,1]
        var seq = list.ToArray();
        await Assert.That(seq).IsEqualTo(new[] { 5, 4, 3, 2, 1 });
    }

    [Test]
    public async Task Enumeration_OnEmptyList_YieldsNothing()
    {
        var list = new SnapshotList<int>();
        await Assert.That(list.Any()).IsFalse();
    }

    [Test]
    public async Task Enumeration_SkipsRemovedItems()
    {
        var list = new SnapshotList<int>();
        list.Add(1);
        list.Add(2);
        list.Add(3); // [3,2,1]
        list.Remove(2);
        await Assert.That(list.ToArray()).IsEqualTo(new[] { 3, 1 });
    }

    [Test]
    public async Task Enumeration_Snapshot_IsolatedFromConcurrentAdd()
    {
        var list = new SnapshotList<int>();
        list.Add(1);
        list.Add(2);
        var enumerator = list.GetEnumerator();
        list.Add(3);
        // snapshot should not include the 3
        var captured = new List<int>();
        while (enumerator.MoveNext())
            captured.Add(enumerator.Current);
        await Assert.That(captured).IsEqualTo(new[] { 2, 1 });
    }

    [Test]
    public async Task MultipleEnumerations_ProduceSameSequence()
    {
        var list = new SnapshotList<int>();
        list.Add(1);
        list.Add(2);
        var first = list.ToArray();
        var second = list.ToArray();
        await Assert.That(second).IsEqualTo(first);
    }

    // ---- Generic/value/reference type tests ----

    [Test]
    public async Task WorksWithReferenceType_String()
    {
        var list = new SnapshotList<string>();
        list.Add("a");
        list.Add("b");
        await Assert.That(list.ToArray()).IsEqualTo(new[] { "b", "a" });
    }

    [Test]
    public async Task WorksWithValueType_Int()
    {
        var list = new SnapshotList<int>();
        list.Add(3);
        await Assert.That(list.Single()).IsEqualTo(3);
    }

    private struct Point
    {
        public int X, Y;
    }

    [Test]
    public async Task WorksWithStructType_Point()
    {
        var list = new SnapshotList<Point>();
        list.Add(new Point { X = 1, Y = 2 });
        var pt = list.Single();
        await Assert.That((pt.X, pt.Y)).IsEqualTo((1, 2));
    }

    [Test]
    public async Task WorksWithNullableValueType_IntNullable()
    {
        var list = new SnapshotList<int?>();
        list.Add(null);
        list.Add(5);
        await Assert.That(list.ToArray()).IsEqualTo(new int?[] { 5, null });
    }

    // ---- Capacity / growth tests ----

    [Test]
    public async Task GrowArray_WhenExceedInitialCapacity_NoErrors()
    {
        var list = new SnapshotList<int>(1);
        for (int i = 0; i < 20; i++) list.Add(i);
        await Assert.That(list.Count()).IsEqualTo(20);
    }

    [Test]
    public async Task AddHundredItems_CapacityGrowsSilently()
    {
        var list = new SnapshotList<int>();
        for (int i = 0; i < 100; i++) list.Add(i);
        await Assert.That(list.Count()).IsEqualTo(100);
    }

    [Test]
    public async Task RemoveAfterGrowth_MaintainsCorrectSequence()
    {
        var list = new SnapshotList<int>();
        for (int i = 0; i < 20; i++) list.Add(i);
        list.Remove(10);
        await Assert.That(list.Contains(10)).IsFalse();
        await Assert.That(list.Count()).IsEqualTo(19);
    }

    // ---- Constructor argument tests ----

    [Test]
    public async Task Constructor_NegativeCapacity_Throws()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new SnapshotList<int>(-5));
    }

    [Test]
    public void Constructor_ZeroCapacity_ThrowsIndexOutOfRange()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new SnapshotList<int>(0));
    }

    [Test]
    public async Task Constructor_PositiveCapacity_NoThrow()
    {
        await Assert.That(() => new SnapshotList<int>(5)).ThrowsNothing();
    }

    [Test]
    public async Task Linq_CountExtension_Matches()
    {
        var list = new SnapshotList<int>();
        list.Add(1);
        list.Add(2);
        await Assert.That(list.Count()).IsEqualTo(2);
    }

    [Test]
    public async Task Linq_FirstExtension_ReturnsHead()
    {
        var list = new SnapshotList<int>();
        list.Add(10);
        list.Add(20); // [20,10]
        await Assert.That(list.First()).IsEqualTo(20);
    }

    [Test]
    public async Task Linq_LastExtension_ReturnsTail()
    {
        var list = new SnapshotList<int>();
        list.Add(5);
        list.Add(6); // [6,5]
        await Assert.That(list.Last()).IsEqualTo(5);
    }

    [Test]
    public async Task Linq_WhereExtension_FiltersCorrectly()
    {
        var list = new SnapshotList<int>();
        list.Add(1);
        list.Add(2);
        list.Add(3);
        var evens = list.Where(x => x % 2 == 0).ToArray();
        await Assert.That(evens).IsEqualTo(new[] { 2 });
    }

    // ---- Custom equality behavior tests ----

    private class Person
    {
        public string? Name { get; init; }

        public override bool Equals(object? obj)
            => obj is Person p && p.Name == Name;

        public override int GetHashCode() => Name?.GetHashCode() ?? 0;
    }

    [Test]
    public async Task Remove_UsesDefaultEqualityComparer_ForCustomClass()
    {
        var list = new SnapshotList<Person>();
        var p1 = new Person { Name = "Alice" };
        var p2 = new Person { Name = "Alice" };
        list.Add(p1);
        // p2 is equal by value, so Remove(p2) should succeed
        await Assert.That(list.Remove(p2)).IsTrue();
        await Assert.That(list.Any()).IsFalse();
    }

    [Test]
    public async Task Remove_ReferenceTypeWithoutOverride_RespectsReferenceEquality()
    {
        var list = new SnapshotList<object>();
        var o1 = new object();
        var o2 = new object();
        list.Add(o1);
        await Assert.That(list.Remove(o2)).IsFalse();
        await Assert.That(list.Remove(o1)).IsTrue();
    }

    // ---- Concurrency tests ----

    [Test]
    public async Task ConcurrentAdd_TenThreads_AllItemsPresent()
    {
        var list = new SnapshotList<int>();
        var threads = new List<Thread>();
        for (int i = 0; i < 10; i++)
        {
            int v = i;
            threads.Add(new Thread(() => list.Add(v)));
        }

        threads.ForEach(t => t.Start());
        threads.ForEach(t => t.Join());
        var result = list.ToArray();
        await Assert.That(result.Length).IsEqualTo(10);
        await Assert.That(result).IsEquivalentTo(Enumerable.Range(0, 10));
    }

    [Test]
    public async Task ConcurrentAdd_HundredThreads_AllItemsPresent()
    {
        var list = new SnapshotList<int>();
        var threads = new List<Thread>();
        for (int i = 0; i < 100; i++)
        {
            int v = i;
            threads.Add(new Thread(() => list.Add(v)));
        }

        threads.ForEach(t => t.Start());
        threads.ForEach(t => t.Join());
        var result = list.ToArray();
        await Assert.That(result.Length).IsEqualTo(100);
        await Assert.That(result).IsEquivalentTo(Enumerable.Range(0, 100));
    }

    [Test]
    public async Task ConcurrentRemove_FiftyThreads_AllItemsRemoved()
    {
        var list = new SnapshotList<int>();
        for (int i = 0; i < 50; i++) list.Add(i);
        var threads = new List<Thread>();
        for (int i = 0; i < 50; i++)
        {
            int v = i;
            await Assert.That(list.Remove(v)).IsTrue();
        }

        threads.ForEach(t => t.Start());
        threads.ForEach(t => t.Join());
        await Assert.That(list.Count()).IsEqualTo(0);
    }

    [Test]
    public async Task Contains_PresentValue_ReturnsTrue()
    {
        var list = new SnapshotList<int>();
        list.Add(1);
        await Assert.That(list.Contains(1)).IsTrue();
    }

    [Test]
    public async Task Contains_AbsentValue_ReturnsFalse()
    {
        var list = new SnapshotList<int>();
        list.Add(2);
        await Assert.That(list.Contains(1)).IsFalse();
    }

    [Test]
    public async Task Contains_AfterAddAndRemove_Correct()
    {
        var list = new SnapshotList<int>();
        list.Add(5);
        list.Remove(5);
        await Assert.That(list.Contains(5)).IsFalse();
    }

    [Test]
    public async Task Count_EmptyList_IsZero()
    {
        var list = new SnapshotList<int>();
        await Assert.That(list.Count()).IsZero();
    }

    [Test]
    public async Task Count_AfterInsertAndRemove_IsCorrect()
    {
        var list = new SnapshotList<int>();
        list.Insert(0, 1);
        list.Insert(1, 2);
        list.Remove(1);
        await Assert.That(list.Count()).IsEqualTo(1);
    }

    [Test]
    public async Task ToArray_EmptyList_ReturnsEmptyArray()
    {
        var list = new SnapshotList<int>();
        await Assert.That(list.ToArray()).IsEmpty();
    }

    [Test]
    public async Task ToArray_AfterOperations_CorrectSequence()
    {
        var list = new SnapshotList<int>();
        list.Add(1);
        list.Insert(1, 2);
        list.Add(3);
        list.Remove(1);
        // operations: Add(1)->[1], Insert(1,2)->[1,2], Add(3)->[3,1,2], Remove(1)->[3,2]
        await Assert.That(list.ToArray()).IsEqualTo(new[] { 3, 2 });
    }

    [Test]
    public async Task RemoveAllOccurrences_Iteratively_RemovesAll()
    {
        var list = new SnapshotList<int>();
        list.Add(1);
        list.Add(1);
        list.Add(1);
        while (list.Remove(1)) { }

        await Assert.That(list.Any()).IsFalse();
    }

    [Test]
    public async Task ReuseSlots_AfterRemove_AddDoesNotGrow()
    {
        var list = new SnapshotList<int>(4);
        // fill to capacity
        for (int i = 0; i < 4; i++) list.Add(i);
        // remove all
        for (int i = 0; i < 4; i++) list.Remove(i);
        // re-add four new items
        for (int i = 10; i < 14; i++) list.Add(i);
        // expect head-first: [13,12,11,10]
        await Assert.That(list.ToArray()).IsEqualTo(new[] { 13, 12, 11, 10 });
    }

    [Test]
    public async Task Stress_AddRemoveCycles_NoExceptions()
    {
        var list = new SnapshotList<int>();
        for (int cycle = 0; cycle < 10; cycle++)
        {
            list.Add(cycle);
            await Assert.That(() => list.Remove(cycle)).ThrowsNothing();
        }

        await Assert.That(list.Any()).IsFalse();
    }

    [Test]
    public async Task EnumerationSnapshot_AfterRemove_RemovesItem()
    {
        var list = new SnapshotList<int>();
        list.Add(1);
        list.Add(2);
        var enumerator = list.GetEnumerator();
        list.Remove(2);
        var captured = new List<int>();
        while (enumerator.MoveNext()) captured.Add(enumerator.Current);
        // snapshot: [2,1]
        await Assert.That(captured).IsEqualTo(new[] { 1 });
    }

    [Test]
    public async Task EnumerationSnapshot_MultipleEnumeratorsIndependent()
    {
        var list = new SnapshotList<int>();
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
        await Assert.That(seq1).IsEqualTo(new[] { 2, 1 });
        await Assert.That(seq2).IsEqualTo(new[] { 2, 1 });
    }

    [Test]
    public async Task GenericEnumerator_Dispose_DoesNotThrow()
    {
        var list = new SnapshotList<int>();
        list.Add(1);
        var en = list.GetEnumerator();
        await Assert.That(() => en.Dispose()).ThrowsNothing();
    }


    [Test]
    public async Task IEnumerableGetEnumerator_ReturnsDifferentInstances()
    {
        var list = new SnapshotList<int>();
        list.Add(1);
        var e1 = list.GetEnumerator();
        var e2 = list.GetEnumerator();
        await Assert.That(e1).IsNotSameReferenceAs(e2);
    }

    [Test]
    public async Task Insert_NullReferenceTypeAtIndex_Works()
    {
        var list = new SnapshotList<string?>();
        list.Add("a");
        list.Insert(1, null);
        await Assert.That(list.ToArray()).IsEqualTo(new[] { "a", null });
    }

    [Test]
    public async Task Insert_ConsecutiveAtSameIndex_PreservesOrder()
    {
        var list = new SnapshotList<string>();
        list.Add("C");
        list.Add("B");
        list.Add("A"); // [A,B,C]
        list.Insert(1, "D"); // [A,D,B,C]
        list.Insert(1, "E"); // [A,E,D,B,C]
        await Assert.That(list.ToArray()).IsEqualTo(new[] { "A", "E", "D", "B", "C" });
    }

    [Test]
    public async Task Insert_AtTailAfterRemovals_Appends()
    {
        var list = new SnapshotList<int>();
        list.Add(1);
        list.Add(2); // [2,1]
        list.Remove(1); // [2]
        list.Insert(1, 3); // append => [2,3]
        await Assert.That(list.ToArray()).IsEqualTo(new[] { 2, 3 });
    }

    [Test]
    public async Task MixedAddInsertRemove_SequenceCorrect()
    {
        var list = new SnapshotList<int>();
        list.Add(10); // [10]
        list.Insert(1, 20); // [10,20]
        list.Add(30); // [30,10,20]
        list.Remove(10); // [30,20]
        await Assert.That(list.ToArray()).IsEqualTo(new[] { 30, 20 });
    }

    [Test]
    public async Task MixedOperations_CountCorrect()
    {
        var list = new SnapshotList<int>();
        list.Add(10);
        list.Insert(1, 20);
        list.Add(30);
        list.Remove(10);
        await Assert.That(list.Count()).IsEqualTo(2);
    }

    [Test]
    public async Task Remove_DefaultValueTypeNotPresent_ReturnsFalse()
    {
        var list = new SnapshotList<int>();
        await Assert.That(list.Remove(default(int))).IsFalse();
    }

    [Test]
    public async Task Remove_DefaultValueTypePresent_ReturnsTrue()
    {
        var list = new SnapshotList<int>();
        list.Add(default(int));
        await Assert.That(list.Remove(default(int))).IsTrue();
    }

    [Test]
    public async Task Add_OneThousandItems_AllPresent()
    {
        var list = new SnapshotList<int>();
        for (int i = 0; i < 1000; i++) list.Add(i);
        await Assert.That(list.Count()).IsEqualTo(1000);
        // spot-check a few
        await Assert.That(list.Contains(0)).IsTrue();
        await Assert.That(list.Contains(999)).IsTrue();
    }

    [Test]
    public async Task Remove_HalfItems_AllRemoved()
    {
        var list = new SnapshotList<int>();
        for (int i = 0; i < 10; i++) list.Add(i); // [9..0]
        for (int i = 0; i < 10; i += 2) list.Remove(i); // remove evens
        await Assert.That(list.ToArray()).IsEqualTo(new[] { 9, 7, 5, 3, 1 });
    }

    [Test]
    public async Task ConcurrentAddRemoveForEachValue_NoRemainingItems()
    {
        var list = new SnapshotList<int>();
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
        await Assert.That(list.Any()).IsFalse();
    }

    [Test]
    public async Task Linq_AnyExtension_ReturnsTrueWhenNotEmpty()
    {
        var list = new SnapshotList<int>();
        list.Add(1);
        await Assert.That(list.Any()).IsTrue();
    }

    [Test]
    public async Task Linq_AnyExtension_ReturnsFalseWhenEmpty()
    {
        var list = new SnapshotList<int>();
        await Assert.That(list.Any()).IsFalse();
    }

    [Test]
    public async Task Linq_ContainsExtension_ReturnsTrue()
    {
        var list = new SnapshotList<int>();
        list.Add(5);
        await Assert.That(list.Contains(5)).IsTrue();
    }

    [Test]
    public async Task Linq_ContainsExtension_ReturnsFalse()
    {
        var list = new SnapshotList<int>();
        await Assert.That(list.Contains(5)).IsFalse();
    }

    [Test]
    public async Task Linq_ElementAt_IndexValid_ReturnsCorrect()
    {
        var list = new SnapshotList<int>();
        list.Add(1);
        list.Add(2); // [2,1]
        await Assert.That(list.ElementAt(0)).IsEqualTo(2);
    }

    [Test]
    public async Task Linq_ElementAt_IndexInvalid_Throws()
    {
        var list = new SnapshotList<int>();
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => list.ElementAt(0));
    }

    [Test]
    public async Task Linq_SkipTake_WorkingOnList()
    {
        var list = new SnapshotList<int>();
        for (int i = 1; i <= 5; i++) list.Add(i); // [5,4,3,2,1]
        var subset = list.Skip(1).Take(3).ToArray(); // [4,3,2]
        await Assert.That(subset).IsEqualTo(new[] { 4, 3, 2 });
    }

    [Test]
    public async Task Linq_Aggregate_CorrectSum()
    {
        var list = new SnapshotList<int>();
        list.Add(1);
        list.Add(2);
        list.Add(3); // [3,2,1]
        var sum = list.Aggregate((a, b) => a + b);
        await Assert.That(sum).IsEqualTo(6);
    }

    [Test]
    public async Task Linq_ToList_Works()
    {
        var list = new SnapshotList<int>();
        list.Add(1);
        list.Add(2);
        var asList = list.ToList();
        await Assert.That(asList).IsEqualTo(list.ToArray());
    }

    [Test]
    public async Task Linq_Max_Works()
    {
        var list = new SnapshotList<int>();
        list.Add(1);
        list.Add(3);
        list.Add(2);
        await Assert.That(list.Max()).IsEqualTo(3);
    }

    [Test]
    public async Task Linq_Min_Works()
    {
        var list = new SnapshotList<int>();
        list.Add(1);
        list.Add(3);
        list.Add(2);
        await Assert.That(list.Min()).IsEqualTo(1);
    }

    [Test]
    public async Task Linq_OrderBy_Works()
    {
        var list = new SnapshotList<int>();
        list.Add(2);
        list.Add(3);
        list.Add(1); // [1,3,2]
        var sorted = list.OrderBy(x => x).ToArray();
        await Assert.That(sorted).IsEqualTo(new[] { 1, 2, 3 });
    }

    [Test]
    public async Task Linq_Distinct_Works()
    {
        var list = new SnapshotList<int>();
        list.Add(1);
        list.Add(2);
        list.Add(2);
        list.Add(3); // [3,2,2,1]
        var distinct = list.Distinct().ToArray();
        await Assert.That(distinct).IsEqualTo(new[] { 3, 2, 1 });
    }

    [Test]
    public async Task Linq_Reverse_Works()
    {
        var list = new SnapshotList<int>();
        list.Add(1);
        list.Add(2);
        list.Add(3); // [3,2,1]
        var rev = list.Reverse().ToArray();
        await Assert.That(rev).IsEqualTo(new[] { 1, 2, 3 });
    }

    [Test]
    public async Task Linq_All_Works()
    {
        var list = new SnapshotList<int>();
        list.Add(2);
        list.Add(4);
        list.Add(6);
        await Assert.That(list.All(x => x % 2 == 0)).IsTrue();
    }

    [Test]
    public async Task Linq_AnyWithPredicate_Works()
    {
        var list = new SnapshotList<int>();
        list.Add(1);
        list.Add(2);
        list.Add(3);
        await Assert.That(list.Any(x => x == 2)).IsTrue();
    }

    [Test]
    public async Task Linq_AnyWithPredicate_NotFound_Works()
    {
        var list = new SnapshotList<int>();
        list.Add(1);
        list.Add(3);
        list.Add(5);
        await Assert.That(list.Any(x => x == 2)).IsFalse();
    }

    [Test]
    public async Task Linq_CountWithPredicate_Works()
    {
        var list = new SnapshotList<int>();
        list.Add(1);
        list.Add(2);
        list.Add(2);
        list.Add(3);
        await Assert.That(list.Count(x => x == 2)).IsEqualTo(2);
    }

    [Test]
    public async Task Linq_SumExtension_Works()
    {
        var list = new SnapshotList<int>();
        list.Add(1);
        list.Add(2);
        list.Add(3);
        await Assert.That(list.Sum()).IsEqualTo(6);
    }

    [Test]
    public async Task Linq_Except_Works()
    {
        var list = new SnapshotList<int>();
        list.Add(1);
        list.Add(2);
        list.Add(3); // [3,2,1]
        var diff = list.Except(new[] { 2 }).ToArray();
        await Assert.That(diff).IsEqualTo(new[] { 3, 1 });
    }

    [Test]
    public async Task Linq_Intersect_Works()
    {
        var list = new SnapshotList<int>();
        list.Add(1);
        list.Add(2);
        list.Add(3); // [3,2,1]
        var inter = list.Intersect(new[] { 2, 3 }).ToArray();
        await Assert.That(inter).IsEqualTo(new[] { 3, 2 });
    }

    [Test]
    public async Task Linq_Union_Works()
    {
        var list = new SnapshotList<int>();
        list.Add(1);
        list.Add(2); // [2,1]
        var uni = list.Union(new[] { 2, 3 }).ToArray();
        await Assert.That(uni).IsEqualTo(new[] { 2, 1, 3 });
    }

    [Test]
    public async Task Linq_Concat_Works()
    {
        var list = new SnapshotList<int>();
        list.Add(1);
        list.Add(2);
        list.Add(3); // [3,2,1]
        var con = list.Concat(new[] { 4, 5 }).ToArray();
        await Assert.That(con).IsEqualTo(new[] { 3, 2, 1, 4, 5 });
    }

    [Test]
    public async Task Remove_AfterRemove_ReturnsFalseSecondTime()
    {
        var list = new SnapshotList<int>();
        list.Add(1);
        await Assert.That(list.Remove(1)).IsTrue();
        await Assert.That(list.Remove(1)).IsFalse();
    }

    [Test]
    public async Task InsertAtBeginning_MultipleTimes_PreservesSequence()
    {
        var list = new SnapshotList<int>();
        for (int i = 0; i < 10; i++)
            list.Insert(0, i);
        await Assert.That(list.ToArray()).IsEqualTo(new[] { 9, 8, 7, 6, 5, 4, 3, 2, 1, 0 });
    }

    /// <summary>
    /// (1) Concurrently keep adding a large number of items so that the internal array must grow several times.
    /// (2) At the same time, repeatedly Remove() a small set of “marked” values.
    /// Finally, verify that all “marked” values are actually gone from the list.
    /// 
    /// If Remove() races with GrowArray incorrectly, some removed items may still appear in enumeration.
    /// </summary>
    [Test]
    public async Task ConcurrentAdd_Remove_DuringResize_ShouldNotLeaveRemovedElementsBehind()
    {
        var list = new SnapshotList<int>(initialCapacity: 2);

        // Pre‐populate with values 1..20 so Remove() has something to delete from the start.
        for (int i = 1; i <= 20; i++)
        {
            list.Add(i);
        }

        // Choose “marked” values that we will continuously try to remove.
        // We expect them to never re-appear once removed.
        var markersToRemove = new[] { 2, 4, 6, 8, 10 };

        // Worker that keeps adding new numbers [100_000 .. 200_000) to force resizing repeatedly.
        var cts = new CancellationTokenSource();
        var addTask = Task.Run(() =>
        {
            int counter = 100_000;
            while (!cts.IsCancellationRequested)
            {
                list.Add(Interlocked.Increment(ref counter));
                // On a fast machine you might want a tiny pause sometimes
                if (counter % 1000 == 0)
                    Thread.Yield();
            }
        }, cts.Token);

        // Worker that keeps removing the “marked” set over and over.
        var removeTask = Task.Run(() =>
        {
            while (!cts.IsCancellationRequested)
            {
                foreach (var m in markersToRemove)
                {
                    list.Remove(m);
                }
            }
        }, cts.Token);

        // Let them run for a short while
        Thread.Sleep(100);

        // Stop both threads
        cts.Cancel();
        Task.WaitAll(new[] { addTask, removeTask }, TimeSpan.FromSeconds(2));

        // Now enumerate the list once and assert that none of the “marked” values remain.
        var snapshot = list.ToArray(); // should capture whatever is left
        foreach (var m in markersToRemove)
        {
            // If the bug described in the analysis is present,
            // you can sometimes still see “m” in snapshot,
            // because Remove() applauded “true” but never unlinked under a concurrent Grow.
            await Assert.That(snapshot).DoesNotContain(m).Because($"Value {m} was supposed to be removed permanently, but we found it in the final snapshot.");
        }
    }

    /// <summary>
    /// Concurrently insert into the middle of a small list and then remove the same “middle” values
    /// while also forcing several array resizes. If Insert and Remove race with a Grow in just the wrong way,
    /// you may see (a) missing insertions or (b) removals that didn’t happen.
    /// </summary>
    [Test]
    public async Task ConcurrentInsertAndRemove_DuringResize_MiddlePositions()
    {
        // Start with initial capacity=4 to force frequent GrowArray calls.
        var list = new SnapshotList<int>(initialCapacity: 4);

        // Seed the list with [1,2,3,4]
        for (int i = 1; i <= 4; i++)
            list.Add(i);

        // We will repeatedly spawn two tasks:
        //   (A) Insert small integers (5..100) at position 2
        //   (B) Remove the same small integers if they exist
        // Meanwhile, keep adding very large integers to force resizing.
        var cts = new CancellationTokenSource();
        var ctsRemove = new CancellationTokenSource();

        // Task A: force resizing by appending large values
        var growTask = Task.Run(() =>
        {
            int bigCounter = 10_000;
            while (!cts.IsCancellationRequested)
            {
                list.Add(Interlocked.Increment(ref bigCounter));
            }
        }, cts.Token);

        // Task B: insert numbers [200..299] always at index=2
        var insertTask = Task.Run(() =>
        {
            for (int i = 200; i < 300 && !cts.IsCancellationRequested; i++)
            {
                try
                {
                    list.Insert(2, i);
                }
                catch (ArgumentOutOfRangeException)
                {
                    // occasionally index=2 might be out of range if list got too small (but that shouldn't normally happen)
                }

                Thread.Yield();
            }
        }, cts.Token);

        // Task C: remove numbers [200..299]
        var removeTask = Task.Run(() =>
        {
            while (!ctsRemove.IsCancellationRequested)
            {
                for (int i = 200; i < 300; i++)
                {
                    list.Remove(i);
                }
            }

            for (int i = 200; i < 300; i++)
            {
                list.Remove(i);
            }
        }, cts.Token);

        // Run for a short period (half a second)
        Thread.Sleep(200);
        cts.Cancel();
        Task.WaitAll(new[] { growTask, insertTask }, TimeSpan.FromSeconds(1));

        // Ensure the remove is done last.
        ctsRemove.Cancel();
        Task.WaitAll(new[] { removeTask }, TimeSpan.FromSeconds(1));

        // We expect that *none* of 200..299 ended up surviving in the final snapshot
        // because every time we insert them, Remove is racing to take them out.
        for (int i = 200; i < 300; i++)
        {
            await Assert.That(list).DoesNotContain(i).Because($"Insert→Remove race should never allow an element {i} to survive after both tasks finish.");
        }
    }

    /// <summary>
    /// Concurrently add a known set of values [0..N) from multiple threads without any removals.
    /// At the end, the list’s Count must be exactly “number_of_inserts”, and every inserted value
    /// must appear exactly once in enumeration (no duplicates, no missing values). This test
    /// will also provoke multiple resizes if N is large compared to initialCapacity.
    /// </summary>
    [Test]
    public async Task ConcurrentAdd_AllDistinctValues_ShouldNotLoseOrDuplicate()
    {
        const int initialCapacity = 8;
        const int threads = 8;
        const int perThread = 1000;
        var list = new SnapshotList<int>(initialCapacity);

        // Each thread will add a disjoint range [tid*perThread .. tid*perThread + (perThread-1)]
        var tasks = new List<Task>();
        for (int tid = 0; tid < threads; tid++)
        {
            int localTid = tid;
            tasks.Add(Task.Run(() =>
            {
                int start = localTid * perThread;
                int end = start + perThread;
                for (int x = start; x < end; x++)
                {
                    list.Add(x);
                }
            }));
        }

        Task.WaitAll(tasks.ToArray(), TimeSpan.FromSeconds(5));

        // The list.Count property is updated with Interlocked.Increment inside Add, so:
        await Assert.That(list.Count).IsEqualTo(threads * perThread).Because("After all concurrent adds finish, Count should exactly match total inserts.");

        // Now enumerate once and check “no duplicates, no missing”:
        var snapshot = list.ToArray();
        // (a) length must match
        await Assert.That(snapshot.Length).IsEqualTo(threads * perThread);

        // (b) every value in [0..threads*perThread) must appear exactly once
        var freq = new int[threads * perThread];
        foreach (int v in snapshot)
        {
            if (v < 0 || v >= threads * perThread)
                Fail.Test($"Found out‐of‐range value {v} in the final snapshot.");
            freq[v]++;
            if (freq[v] > 1)
                Fail.Test($"Duplicate value {v} found in final snapshot.");
        }

        for (int i = 0; i < freq.Length; i++)
        {
            if (freq[i] != 1)
                Fail.Test($"Missing value {i} in the final snapshot (freq={freq[i]}).");
        }
    }

    /// <summary>
    /// Launches multiple tasks, each of which does many Add(...) calls
    /// to a very small‐capacity LockFreeArrayList. If Add is using a stale
    /// array reference during a resize, some items will vanish. We assert
    /// that, after all tasks complete, the list contains exactly all items.
    /// </summary>
    [Test]
    [Repeat(10)]
    public async Task Add_DuringConcurrentResize_DoesNotLoseItems()
    {
        // Arrange: start with a very small initial capacity so that we force at least one GrowArray
        var list = new SnapshotList<int>(initialCapacity: 2);

        const int threadCount = 4;
        const int itemsPerThread = 2000;
        var expectedItems = new List<int>(threadCount * itemsPerThread);
        var tasks = new Task[threadCount];

        // We assign each thread a non‑overlapping block of integers so that we can verify exactly
        // which items must appear.
        for (int t = 0; t < threadCount; t++)
        {
            int threadIndex = t;
            tasks[t] = Task.Run(() =>
            {
                int baseValue = threadIndex * itemsPerThread;
                for (int i = 0; i < itemsPerThread; i++)
                {
                    int item = baseValue + i;
                    list.Add(item);
                }
            });

            // Record what we expect to see in the final enumeration
            expectedItems.AddRange(Enumerable.Range(t * itemsPerThread, itemsPerThread));
        }

        // Act: wait for all threads to finish
        Task.WaitAll(tasks);

        // Grab everything in the list via enumeration (note: order is not guaranteed)
        var actualItems = list.ToList();

        // Assert #1: the list reports the correct Count
        await Assert.That(actualItems.Count).IsEqualTo(expectedItems.Count).Because($"Expected {expectedItems.Count} items, but found {actualItems.Count} in enumeration.");

        // Assert #2: every single expected integer appears exactly once
        await Assert.That(actualItems).IsEquivalentTo(expectedItems).Because("The enumeration of items did not match the exact set of values inserted.");
    }

    [Test]
    public async Task MultipleEnumerators_DifferentSnapshots_ShowCorrectState()
    {
        var list = new SnapshotList<int>();
        list.Add(1);
        list.Add(2);

        var enum1 = list.GetEnumerator();
        list.Add(3);
        var enum2 = list.GetEnumerator();
        list.Add(4);
        var enum3 = list.GetEnumerator();

        var snapshot1 = new List<int>();
        var snapshot2 = new List<int>();
        var snapshot3 = new List<int>();

        while (enum1.MoveNext()) snapshot1.Add(enum1.Current);
        while (enum2.MoveNext()) snapshot2.Add(enum2.Current);
        while (enum3.MoveNext()) snapshot3.Add(enum3.Current);

        await Assert.That(snapshot1).IsEqualTo(new[] { 2, 1 });
        await Assert.That(snapshot2).IsEqualTo(new[] { 3, 2, 1 });
        await Assert.That(snapshot3).IsEqualTo(new[] { 4, 3, 2, 1 });
    }


    [Test]
    public async Task ConcurrentInsert_AtSameIndex_AllItemsPresent()
    {
        var list = new SnapshotList<int>();
        list.Add(1000);
        list.Add(2000);

        const int threadCount = 10;
        const int itemsPerThread = 100;
        var tasks = new Task[threadCount];

        for (int t = 0; t < threadCount; t++)
        {
            int threadId = t;
            tasks[t] = Task.Run(() =>
            {
                for (int i = 0; i < itemsPerThread; i++)
                {
                    list.Insert(1, threadId * 1000 + i);
                }
            });
        }

        Task.WaitAll(tasks);

        var result = list.ToArray();
        await Assert.That(result.Length).IsEqualTo(2 + threadCount * itemsPerThread);
    }
    
    [Test]
    public async Task ConcurrentAddRemove_SameItem_ConsistentState()
    {
        var list = new SnapshotList<int>();
        const int iterations = 1000;
        var addCount = 0;
        var removeCount = 0;
    
        var addTask = Task.Run(() =>
        {
            for (int i = 0; i < iterations; i++)
            {
                list.Add(42);
                Interlocked.Increment(ref addCount);
            }
        });
    
        var removeTask = Task.Run(() =>
        {
            for (int i = 0; i < iterations; i++)
            {
                if (list.Remove(42))
                    Interlocked.Increment(ref removeCount);
                Thread.Yield();
            }
        });
    
        Task.WaitAll(addTask, removeTask);
    
        var finalCount = list.Count(x => x == 42);
        await Assert.That(finalCount).IsEqualTo(addCount - removeCount);
    }
    
    [Test]
    public async Task Insert_DuringGrowArray_MaintainsOrder()
    {
        var list = new SnapshotList<int>(initialCapacity: 2);
    
        // Fill to near capacity
        list.Add(1);
    
        var tasks = new Task[5];
        for (int i = 0; i < 5; i++)
        {
            int value = (i + 1) * 100;
            tasks[i] = Task.Run(() =>
            {
                for (int j = 0; j < 100; j++)
                {
                    list.Insert(0, value + j);
                }
            });
        }
    
        Task.WaitAll(tasks);
    
        var result = list.ToArray();
        await Assert.That(result.Length).IsEqualTo(501); // 1 initial + 500 inserted
    }
    
    [Test]
    public async Task ConcurrentRemove_NonExistentItems_NoDeadlock()
    {
        var list = new SnapshotList<int>();
        for (int i = 0; i < 10; i++) list.Add(i);
    
        var tasks = new Task[20];
        for (int t = 0; t < 20; t++)
        {
            int value = t + 100; // Non-existent values
            tasks[t] = Task.Run(() =>
            {
                for (int i = 0; i < 100; i++)
                {
                    list.Remove(value);
                }
            });
        }
    
        var completed = Task.WaitAll(tasks, TimeSpan.FromSeconds(5));
        await Assert.That(completed).IsTrue().Because("Tasks should complete without deadlock");
    }
    
    [Test]
    public async Task FreeList_ConcurrentReuseSlots_NoCorruption()
    {
        var list = new SnapshotList<int>(initialCapacity: 4);
    
        var addRemoveTask = Task.Run(() =>
        {
            for (int i = 0; i < 1000; i++)
            {
                list.Add(i);
                list.Remove(i);
            }
        });
    
        var addTask = Task.Run(() =>
        {
            for (int i = 1000; i < 2000; i++)
            {
                list.Add(i);
            }
        });
    
        Task.WaitAll(addRemoveTask, addTask);
    
        var result = list.ToArray();
        await Assert.That(result.All(x => x >= 1000 && x < 2000)).IsTrue();
    }
    
    [Test]
    public async Task Snapshot_BeforeAndAfterGrowArray_BothValid()
    {
        var list = new SnapshotList<int>(initialCapacity: 2);
        list.Add(1);
    
        var enum1 = list.GetEnumerator();
    
        // Force multiple grow operations
        for (int i = 2; i <= 100; i++)
            list.Add(i);
    
        var enum2 = list.GetEnumerator();
    
        var snapshot1 = new List<int>();
        var snapshot2 = new List<int>();
    
        while (enum1.MoveNext()) snapshot1.Add(enum1.Current);
        while (enum2.MoveNext()) snapshot2.Add(enum2.Current);
    
        await Assert.That(snapshot1).IsEqualTo(new[] { 1 });
        await Assert.That(snapshot2.Count).IsEqualTo(100);
    }
    
    [Test]
    public async Task ConcurrentEnumeration_WithActiveModifications_NoInterference()
    {
        var list = new SnapshotList<int>();
        for (int i = 0; i < 50; i++) list.Add(i);
    
        var enumerationComplete = new ManualResetEventSlim(false);
        var modificationsComplete = new ManualResetEventSlim(false);
    
        var enumTask = Task.Run(() =>
        {
            var snapshot = list.ToArray();
            enumerationComplete.Set();
            modificationsComplete.Wait();
        
            // Re-enumerate after modifications
            var snapshot2 = list.ToArray();
            CallbackAssert.Equal(snapshot.Length, 50);
        });
    
        var modTask = Task.Run(() =>
        {
            enumerationComplete.Wait();
            for (int i = 0; i < 25; i++)
            {
                list.Remove(i);
                list.Add(i + 100);
            }
            modificationsComplete.Set();
        });
    
        Task.WaitAll(enumTask, modTask);
    }
    
    [Test]
    public async Task Remove_LastElementWhileEnumerating_EnumeratorCompletes()
    {
        var list = new SnapshotList<int>();
        list.Add(1);
    
        var enumerator = list.GetEnumerator();
        list.Remove(1);
    
        await Assert.That(enumerator.MoveNext()).IsFalse();
    }
    
    [Test]
    public async Task ConcurrentInsertRemove_HighContention_MaintainsIntegrity()
    {
        var list = new SnapshotList<int>(initialCapacity: 4);
        var tasks = new Task[10];
        var barrier = new Barrier(tasks.Length);
        
        for (int t = 0; t < tasks.Length; t++)
        {
            int threadId = t;
            tasks[t] = Task.Run(() =>
            {
                barrier.SignalAndWait(); // Ensure all threads start together
            
                for (int i = 0; i < 100; i++)
                {
                    if (threadId % 2 == 0)
                    {
                        list.Add(threadId * 1000 + i);
                        list.Remove(threadId * 1000 + i - 1);
                    }
                    else
                    {
                        try { list.Insert(0, threadId * 1000 + i); }
                        catch (ArgumentOutOfRangeException) { }
                    }
                }
            });
        }
    
        Task.WaitAll(tasks);
    
        // Verify we can still enumerate
        var count = 0;
        foreach (var item in list)
            count++;
    
        await Assert.That(count).IsGreaterThanOrEqualTo(0);
    }
    
    [Test]
    public async Task FreeList_StressTest_NoMemoryLeak()
    {
        var list = new SnapshotList<int>(initialCapacity: 4);
        const int cycles = 10000;
    
        // Repeatedly add and remove to stress the free list
        for (int i = 0; i < cycles; i++)
        {
            list.Add(i);
            await Assert.That(list.Remove(i)).IsTrue();
        }
    
        // The count should be low, showing slots are being reused
        await Assert.That(list.Count).IsEqualTo(0);

        // Add items again to verify free list works
        for (int i = 0; i < 10; i++)
            list.Add(i);
    
        await Assert.That(list.Count()).IsEqualTo(10);
    }

}
