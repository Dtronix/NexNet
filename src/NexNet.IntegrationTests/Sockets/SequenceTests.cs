using System.Buffers;
using NexNet.Internals.Pipelines.Arenas;
using NUnit.Framework;

namespace NexNet.IntegrationTests.Sockets
{
    [TestFixture]
    public class SequenceTests
    {
        [Test]
        public void CheckDefaultSequence()
        {
            Sequence<int> seq = default;
            Assert.That(seq.IsArray, Is.False);
            Assert.That(seq.IsSingleSegment, Is.True);
            TestEveryWhichWay(seq, 0);
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        [TestCase(42)]
        [TestCase(1024)]
        public void CheckArray(int length)
        {
            Sequence<int> seq = new Sequence<int>(new int[length]);
            Assert.That(seq.IsArray, Is.True);
            Assert.That(seq.IsSingleSegment, Is.True);
            TestEveryWhichWay(seq, length);
        }

        [Test]
        public void CheckDefaultMemory()
        {
            Memory<int> memory = default;
            Sequence<int> seq = new Sequence<int>(memory);
            Assert.That(seq.IsArray, Is.True);
            Assert.That(seq.IsSingleSegment, Is.True);
            TestEveryWhichWay(seq, 0);
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        [TestCase(42)]
        [TestCase(1024)]
        public void CheckArrayBackedMemory(int length)
        {
            Memory<int> memory = new int[length];
            Sequence<int> seq = new Sequence<int>(memory);
            Assert.That(seq.IsArray, Is.True);
            Assert.That(seq.IsSingleSegment, Is.True);
            TestEveryWhichWay(seq, length);
        }

        private class MyManager : MemoryManager<int>
        {
            private readonly Memory<int> _memory;
#pragma warning disable RCS1231 // Make parameter ref read-only.
            public MyManager(Memory<int> memory) => _memory = memory;
#pragma warning restore RCS1231 // Make parameter ref read-only.
            public override Span<int> GetSpan() => _memory.Span;

            public override MemoryHandle Pin(int elementIndex = 0) => default;

            public override void Unpin() { }

            protected override void Dispose(bool disposing) { }
        }

        [Test]
        public void CheckDefaultCustomManager()
        {
            Memory<int> memory = default;
            using var owner = new MyManager(memory);
            Sequence<int> seq = new Sequence<int>(owner.Memory);
            Assert.That(seq.IsArray, Is.False);
            Assert.That(seq.IsSingleSegment, Is.True);
            TestEveryWhichWay(seq, 0);
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        [TestCase(42)]
        [TestCase(1024)]
        public void CheckArrayBackedCustomManager(int length)
        {
            Memory<int> memory = new int[length];
            using var owner = new MyManager(memory);
            Sequence<int> seq = new Sequence<int>(owner.Memory);
            Assert.That(seq.IsArray, Is.False);
            Assert.That(seq.IsSingleSegment, Is.True);
            TestEveryWhichWay(seq, length);
        }

        private unsafe class MyUnsafeManager : MemoryManager<int>
        {
            protected readonly int* _ptr;
            protected readonly int _length;
            public MyUnsafeManager(int* ptr, int length)
            {
                _ptr = ptr;
                _length = length;
            }
            public override Span<int> GetSpan() => new Span<int>(_ptr, _length);

            public override MemoryHandle Pin(int elementIndex = 0) => default;

            public override void Unpin() { }

            protected override void Dispose(bool disposing) { }
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        [TestCase(42)]
        [TestCase(1024)]
        public unsafe void CheckUnsafeCustomManager(int length)
        {
            int* ptr = stackalloc int[length];
            using var owner = new MyUnsafeManager(ptr, length);
            Sequence<int> seq = new Sequence<int>(owner.Memory);
            Assert.That(seq.IsArray, Is.False);
            Assert.That(seq.IsPinned, Is.False);
            Assert.That(seq.IsSingleSegment, Is.True);
            TestEveryWhichWay(seq, length);
        }

        private unsafe class MyUnsafePinnedManager : MyUnsafeManager, IPinnedMemoryOwner<int>
        {
            public MyUnsafePinnedManager(int* ptr, int length) : base(ptr, length) { }

            public void* Origin => _ptr;

            public int Length => _length;
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        [TestCase(42)]
        [TestCase(1024)]
        public unsafe void CheckUnsafePinnedCustomManager(int length)
        {
            int* ptr = stackalloc int[length + 1]; // extra to ensure never nil
            using var owner = new MyUnsafePinnedManager(ptr, length);
            Sequence<int> seq = new Sequence<int>(owner.Memory);
            Assert.That(seq.IsArray, Is.False);
            Assert.That(seq.IsPinned, Is.True);
            Assert.That(seq.IsSingleSegment, Is.True);
            TestEveryWhichWay(seq, length);
        }

        unsafe private class MySegment : SequenceSegment<int>, IPinnedMemoryOwner<int>
        {
            public void* Origin { get; }
#pragma warning disable RCS1231 // Make parameter ref read-only.
            public MySegment(Memory<int> memory, MySegment? previous = null) : base(memory, previous) { }
#pragma warning restore RCS1231 // Make parameter ref read-only.
            public MySegment(IMemoryOwner<int> owner, MySegment? previous = null) : base(owner.Memory, previous)
            {
                if (owner is IPinnedMemoryOwner<int> pinned) Origin = pinned.Origin;
            }
        }

        [Test]
        public void CheckDefaultSegments()
        {
            var first = new MySegment(memory: default);
            var seq = new Sequence<int>(first, first, 0, 0);
            Assert.That(seq.IsArray, Is.False);
            Assert.That(seq.IsPinned, Is.False);
            Assert.That(seq.IsSingleSegment, Is.True);

            TestEveryWhichWay(seq, 0);
        }

        [TestCase(new int[] { 0 }, true)]
        [TestCase(new int[] { 1 }, true)]
        [TestCase(new int[] { 2 }, true)]
        [TestCase(new int[] { 42 }, true)]
        [TestCase(new int[] { 1024 }, true)]
        // test roll forward
        [TestCase(new int[] { 0, 0 }, true)]
        [TestCase(new int[] { 0, 1 }, true)]
        [TestCase(new int[] { 0, 2 }, true)]
        [TestCase(new int[] { 0, 42 }, true)]
        [TestCase(new int[] { 0, 1024 }, true)]
        // test roll backward
        [TestCase(new int[] { 1, 0 }, true)]
        [TestCase(new int[] { 2, 0 }, true)]
        [TestCase(new int[] { 42, 0 }, true)]
        [TestCase(new int[] { 1024, 0 }, true)]
        // test non-trivial
        [TestCase(new int[] { 128, 128, 64 }, false)]
        [TestCase(new int[] { 128, 0, 64, 0, 12 }, false)] // zero length blocks in the middle
        [TestCase(new int[] { 0, 128, 0, 64, 0 }, false)] // zero length blocks at the ends
        [TestCase(new int[] { 0, 128, 0 }, true)]

        public void CheckArrayBackedSegments(int[] sizes, bool isSingleSegment)
        {
            static Memory<int> Create(int size)
            {
                return new int[size];
            }
            int length = sizes.Sum();
            var first = new MySegment(Create(sizes[0]));
            var last = first;
            for (int i = 1; i < sizes.Length; i++)
            {
                last = new MySegment(Create(sizes[i]), last);
            }
            Sequence<int> seq = new Sequence<int>(first, last, 0, last.Length);
            Assert.That(seq.IsArray, Is.False);
            Assert.That(seq.IsPinned, Is.False);

            Assert.That(seq.IsSingleSegment, Is.EqualTo(isSingleSegment));
            TestEveryWhichWay(seq, length);
        }

        [TestCase(new int[] { 0 }, true)]
        [TestCase(new int[] { 1 }, true)]
        [TestCase(new int[] { 2 }, true)]
        [TestCase(new int[] { 42 }, true)]
        [TestCase(new int[] { 1024 }, true)]
        // test roll forward
        [TestCase(new int[] { 0, 0 }, true)]
        [TestCase(new int[] { 0, 1 }, true)]
        [TestCase(new int[] { 0, 2 }, true)]
        [TestCase(new int[] { 0, 42 }, true)]
        [TestCase(new int[] { 0, 1024 }, true)]
        // test roll backward
        [TestCase(new int[] { 1, 0 }, true)]
        [TestCase(new int[] { 2, 0 }, true)]
        [TestCase(new int[] { 42, 0 }, true)]
        [TestCase(new int[] { 1024, 0 }, true)]
        // test non-trivial
        [TestCase(new int[] { 128, 128, 64 }, false)]
        [TestCase(new int[] { 128, 0, 64, 0, 12 }, false)] // zero length blocks in the middle
        [TestCase(new int[] { 0, 128, 0, 64, 0 }, false)] // zero length blocks at the ends
        [TestCase(new int[] { 0, 128, 0 }, true)]
        public unsafe void CheckUnsafeBackedSegments(int[] sizes, bool isSingleSegment)
        {
            int length = sizes.Sum();
            int* ptr = stackalloc int[length + 1]; // extra to ensure never nil

            IMemoryOwner<int> Create(int size)
            {
                var mem = new MyUnsafeManager(ptr, size);
                ptr += size;
                return mem;
            }

            var first = new MySegment(Create(sizes[0]));
            var last = first;
            for (int i = 1; i < sizes.Length; i++)
            {
                last = new MySegment(Create(sizes[i]), last);
            }
            Sequence<int> seq = new Sequence<int>(first, last, 0, last.Length);
            Assert.That(seq.IsArray, Is.False);
            Assert.That(seq.IsPinned, Is.False);

            Assert.That(seq.IsSingleSegment, Is.EqualTo(isSingleSegment));
            TestEveryWhichWay(seq, length);
        }

        [TestCase(new int[] { 0 }, true)]
        [TestCase(new int[] { 1 }, true)]
        [TestCase(new int[] { 2 }, true)]
        [TestCase(new int[] { 42 }, true)]
        [TestCase(new int[] { 1024 }, true)]
        // test roll forward
        [TestCase(new int[] { 0, 0 }, true)]
        [TestCase(new int[] { 0, 1 }, true)]
        [TestCase(new int[] { 0, 2 }, true)]
        [TestCase(new int[] { 0, 42 }, true)]
        [TestCase(new int[] { 0, 1024 }, true)]
        // test roll backward
        [TestCase(new int[] { 1, 0 }, true)]
        [TestCase(new int[] { 2, 0 }, true)]
        [TestCase(new int[] { 42, 0 }, true)]
        [TestCase(new int[] { 1024, 0 }, true)]
        //test non-trivial
        [TestCase(new int[] { 128, 128, 64 }, false)]
        [TestCase(new int[] { 128, 0, 64, 0, 12 }, false)] // zero length blocks in the middle
        [TestCase(new int[] { 0, 128, 0, 64, 0 }, false)] // zero length blocks at the ends
        [TestCase(new int[] { 0, 128, 0 }, true)]
        public unsafe void CheckUnsafePinnedBackedSegments(int[] sizes, bool isSingleSegment)
        {
            int length = sizes.Sum();
            int* ptr = stackalloc int[length + 1]; // extra to ensure never nil

            IMemoryOwner<int> Create(int size)
            {
                var mem = new MyUnsafePinnedManager(ptr, size);
                ptr += size;
                return mem;
            }

            var first = new MySegment(Create(sizes[0]));
            var last = first;
            for (int i = 1; i < sizes.Length; i++)
            {
                last = new MySegment(Create(sizes[i]), last);
            }
            Sequence<int> seq = new Sequence<int>(first, last, 0, last.Length);
            Assert.That(seq.IsArray, Is.False);
            Assert.That(seq.IsPinned, Is.True);

            Assert.That(seq.IsSingleSegment, Is.EqualTo(isSingleSegment));
            TestEveryWhichWay(seq, length);
        }

        private void TestEveryWhichWay(Sequence<int> sequence, int count)
        {
            Random? rand = null; //int _nextRandom = 0;
            int GetNextRandom() => rand!.Next(0, 100); //_nextRandom++;
            void ResetRandom() => rand = new Random(12345); // _nextRandom = 0;

            Assert.That(sequence.Length, Is.EqualTo(count));
            if (!sequence.IsEmpty)
            {
                ResetRandom();
                var filler = sequence.GetEnumerator();
                while (filler.MoveNext())
                    filler.Current = GetNextRandom();
            }
            // count/sum via the item iterator
            long total = 0, t;
            int c = 0;
            ResetRandom();
            foreach (var item in sequence)
            {
                c++;
                total += item;
                Assert.That(item, Is.EqualTo(GetNextRandom()));
            }
            Assert.That(c, Is.EqualTo(count));

            if (count == 0) Assert.That(sequence.IsEmpty, Is.True);
            else Assert.That(sequence.IsEmpty, Is.False);

            // count/sum via the span iterator
            t = 0;
            c = 0;
            int spanCount = 0;
            ResetRandom();
            foreach (var span in sequence.Spans)
            {
                if (!span.IsEmpty) spanCount++; // ROS always returns at least one, so...
                foreach (var item in span)
                {
                    c++;
                    t += item;
                    Assert.That(item, Is.EqualTo(GetNextRandom()));
                }
            }
            Assert.That(t, Is.EqualTo(total));
            Assert.That(c, Is.EqualTo(count));

            if (spanCount <= 1) Assert.That(sequence.IsSingleSegment, Is.True);
            else Assert.That(sequence.IsSingleSegment, Is.False);

            // count/sum via the segment iterator
            t = 0;
            c = 0;
            int memoryCount = 0;
            ResetRandom();
            foreach (var memory in sequence.Segments)
            {
                if (!memory.IsEmpty) memoryCount++;
                foreach (var item in memory.Span)
                {
                    c++;
                    t += item;
                    Assert.That(item, Is.EqualTo(GetNextRandom()));
                }
            }
            Assert.That(t, Is.EqualTo(total));
            Assert.That(c, Is.EqualTo(count));
            Assert.That(memoryCount, Is.EqualTo(spanCount));

            // count/sum via reference iterator
            ResetRandom();
            var iter = sequence.GetEnumerator();
            t = 0;
            c = 0;
            while (iter.MoveNext())
            {
                c++;
                t += iter.Current;
                Assert.That(iter.Current, Is.EqualTo(GetNextRandom()));
            }
            Assert.That(t, Is.EqualTo(total));
            Assert.That(c, Is.EqualTo(count));

            // count/sum via GetNext();
            ResetRandom();
            iter = sequence.GetEnumerator();
            t = 0;
            c = 0;
            for (long index = 0; index < count; index++)
            {
                c++;
                var n = iter.GetNext();
                t += n;
                Assert.That(n, Is.EqualTo(GetNextRandom()));
            }
            try
            {
                iter.GetNext(); // this should throw (can't use Assert.Throws here because ref-struct)
                Assert.Throws<IndexOutOfRangeException>(() => { });
            }
            catch (IndexOutOfRangeException) { }
            Assert.That(t, Is.EqualTo(total));
            Assert.That(c, Is.EqualTo(count));

            // count/sum via indexer
            t = 0;
            c = 0;
            ResetRandom();
            for (long index = 0; index < count; index++)
            {
                c++;
                t += sequence[index];
                Assert.That(sequence[index], Is.EqualTo(GetNextRandom()));
            }
            Assert.Throws<IndexOutOfRangeException>(() => { var _ = sequence[-1]; });
            Assert.Throws<IndexOutOfRangeException>(() => { var _ = sequence[c]; });
            Assert.That(t, Is.EqualTo(total));
            Assert.That(c, Is.EqualTo(count));

            // count/sum via Reference<T>
            t = 0;
            c = 0;
            ResetRandom();
            for (long index = 0; index < count; index++)
            {
                c++;
                var r = sequence.GetReference(index);
                t += r.Value;
                Assert.That((int)r, Is.EqualTo(GetNextRandom()));
            }
            Assert.Throws<IndexOutOfRangeException>(() => { var _ = sequence[-1]; });
            Assert.Throws<IndexOutOfRangeException>(() => { var _ = sequence[c]; });
            Assert.That(t, Is.EqualTo(total));
            Assert.That(c, Is.EqualTo(count));

            // count/sum via list using struct iterator
            t = 0;
            c = 0;
            ResetRandom();
            var list = sequence.ToList();
            foreach (var item in list)
            {
                c++;
                t += item;
                Assert.That(item, Is.EqualTo(GetNextRandom()));
            }
            Assert.That(t, Is.EqualTo(total));
            Assert.That(c, Is.EqualTo(count));

            // count/sum via list using object iterator
            t = 0;
            c = 0;
            ResetRandom();
            foreach (var item in list.AsEnumerable())
            {
                c++;
                t += item;
                Assert.That(item, Is.EqualTo(GetNextRandom()));
            }
            Assert.That(t, Is.EqualTo(total));
            Assert.That(c, Is.EqualTo(count));

            // check by list index
            Assert.That(list.Count, Is.EqualTo(c));
            ResetRandom();
            for (int i = 0; i < count; i++)
            {
                Assert.That(list[i], Is.EqualTo(sequence[i]));
                Assert.That(list[i], Is.EqualTo(GetNextRandom()));
            }
            Assert.Throws<IndexOutOfRangeException>(() => { var _ = list[-1]; });
            Assert.Throws<IndexOutOfRangeException>(() => { var _ = list[c]; });

            // count/sum via list using GetReference
            t = 0;
            c = 0;
            for (long index = 0; index < count; index++)
            {
                c++;
                t += sequence.GetReference(index);
            }
            Assert.That(t, Is.EqualTo(total));
            Assert.That(c, Is.EqualTo(count));
            Assert.Throws<IndexOutOfRangeException>(() => sequence.GetReference(-1));
            Assert.Throws<IndexOutOfRangeException>(() => sequence.GetReference(c));

            // check positions are obtainable
            for (long index = 0; index <= count; index++)
            {
                sequence.GetPosition(index);
            }
            Assert.Throws<IndexOutOfRangeException>(() => sequence.GetPosition(-1));
            Assert.Throws<IndexOutOfRangeException>(() => sequence.GetPosition(c + 1));

            // get ROS; note: we won't attempt to compare ROS and S positions,
            // as positions are only meaningful inside the context in which they
            // are obtained - we can check the slice contents one at a time, though
            var ros = sequence.AsReadOnly();
            Assert.That(ros.Length, Is.EqualTo(c));
            for (int i = 0; i <= count; i++)
            {
                var roSlice = ros.Slice(i, 0);
                var slice = sequence.Slice(i, 0);
                Assert.That(slice.Length, Is.EqualTo(roSlice.Length));
            }
            for (int i = 0; i < count; i++)
            {
                var roSlice = ros.Slice(i, 1);
                var slice = sequence.Slice(i, 1);
                Assert.That(slice.Length, Is.EqualTo(roSlice.Length));
                Assert.That(slice[0], Is.EqualTo(roSlice.First.Span[0]));
            }

            // and get back again
            Assert.That(Sequence<int>.TryGetSequence(ros, out var andBackAgain), Is.True);
            Assert.That(andBackAgain, Is.EqualTo(sequence));

            // count/sum via list using ROS
            t = 0;
            c = 0;
            int roSpanCount = 0;
            foreach (var memory in ros)
            {
                if (!memory.IsEmpty) roSpanCount++;
                foreach (int item in memory.Span)
                {
                    c++;
                    t += item;
                }
            }
            Assert.That(t, Is.EqualTo(total));
            Assert.That(c, Is.EqualTo(count));
            Assert.That(roSpanCount, Is.EqualTo(spanCount));

            static void AssertEqualExceptMSB(in SequencePosition expected, in SequencePosition actual)
            {
                object? eo = expected.GetObject(), ao = actual.GetObject();
                int ei = expected.GetInteger() & ~Sequence.IsArrayFlag,
                    ai = actual.GetInteger() & ~Sequence.IsArrayFlag;

                Assert.That(ai, Is.EqualTo(ei));
                Assert.That(ao, Is.EqualTo(eo));
            }

            // slice everything
            t = 0;
            c = 0;
            ResetRandom();
            for (int i = 0; i < count; i++)
            {
                var pos = sequence.GetPosition(i);
                var slice = sequence.Slice(i, 0);

                Assert.That(slice.IsEmpty, Is.True);
                AssertEqualExceptMSB(pos, slice.Start);
                AssertEqualExceptMSB(slice.Start, slice.End);

                slice = sequence.Slice(i);
                Assert.That(slice.Length, Is.EqualTo(count - i));
                AssertEqualExceptMSB(pos, slice.Start);
                Assert.That(slice.End, Is.EqualTo(sequence.End));

                slice = sequence.Slice(0, i);
                Assert.That(slice.Length, Is.EqualTo(i));
                Assert.That(slice.Start, Is.EqualTo(sequence.Start));
                AssertEqualExceptMSB(pos, slice.End);

                slice = sequence.Slice(i, 1);
                Assert.That(slice.Length, Is.EqualTo(1));
                AssertEqualExceptMSB(pos, slice.Start);
                AssertEqualExceptMSB(sequence.GetPosition(i + 1), slice.End);

                t += slice[0];
                c += (int)slice.Length; // 1
                Assert.That(slice[0], Is.EqualTo(GetNextRandom()));
            }
            Assert.That(c, Is.EqualTo(count));
            Assert.That(t, Is.EqualTo(total));

            Assert.Throws<ArgumentOutOfRangeException>(() => sequence.Slice(-1, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => sequence.Slice(c, 1));

            var end = sequence.Slice(0, 0);
            Assert.That(end.IsEmpty, Is.True);
            Assert.That(end.Start, Is.EqualTo(sequence.Start));
            AssertEqualExceptMSB(sequence.Start, end.End);

            end = sequence.Slice(c, 0);
            Assert.That(end.IsEmpty, Is.True);
            AssertEqualExceptMSB(sequence.End, end.Start);
            Assert.That(end.End, Is.EqualTo(sequence.End));
        }
    }
}
