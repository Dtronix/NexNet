using System.Buffers;
using System.Text;
using NexNet.Internals.Pipelines.Arenas;
using NexNet.Internals.Pipelines.Buffers;
using NUnit.Framework;

namespace NexNet.IntegrationTests.Sockets
{
    [TestFixture]
    public class BufferWriterTests
    {
        private readonly BufferedTestLogger _logger = new();

        [SetUp]
        public void SetUp()
        {
            _logger.Clear();
        }

        [TearDown]
        public void TearDown()
        {
            _logger.FlushOnFailure();
        }

        static string Raw(ReadOnlySequence<byte> values)
        {
            // this doesn't need to be efficient, just correct
            var chars = ArrayPool<char>.Shared.Rent((int)values.Length);
            int offset = 0;
            foreach(var segment in values)
            {
                var span = segment.Span;
                for (int i = 0; i < span.Length; i++)
                    chars[offset++] = (char)('0' + span[i]);
            }
            return new string(chars, 0, (int)values.Length);
        }

        [Test]
        public void CanPartialFlush()
        {
            using var bw = BufferWriter<byte>.Create(blockSize: 16);
            bw.GetSequence(128);
            bw.Advance(50);
            bw.Advance(30);

            Assert.That(bw.Length, Is.EqualTo(80));

            using var x1 = bw.Flush(20);
            Assert.That(x1.Value.Length, Is.EqualTo(20));
            Assert.That(bw.Length, Is.EqualTo(60));
            using var x2 = bw.Flush();
            Assert.That(x2.Value.Length, Is.EqualTo(60));
            Assert.That(bw.Length, Is.EqualTo(0));
        }

        /*
        [Test]
        public void BufferWriterDoesNotLeak()
        {
#pragma warning disable IDE0063 // this would break the "all dead now" test
            using (var bw = BufferWriter<byte>.Create(blockSize: 16))
#pragma warning restore IDE0063
            {
                var writer = bw.Writer;

                byte nextVal = 0;
                Owned<ReadOnlySequence<byte>> Write(int count)
                {
                    for (int i = 0; i < count; i++)
                    {
                        var span = writer.GetSpan(5);
                        span.Fill(nextVal++);
                        writer.Advance(5);
                    }
                    _logger.Log($"before flush, wrote {count * 5}... {bw.GetState()}");
                    var result = bw.Flush();
                    _logger.Log($"after flush: {bw.GetState()}");
                    return result;
                }

                var chunks = new Owned<ReadOnlySequence<byte>>[5];
                var rand = new Random(1234);
                for (int i = 0; i < chunks.Length; i++)
                {
                    // note that the lifetime of the chunks are completely independent
                    // and can be disposed arbitrarily
                    var chunk = Write(i);
                    ReadOnlySequence<byte> ros = chunk;
                    Assert.That(ros.Length, Is.EqualTo(i * 5));

                    chunks[i] = chunk;
                }

                Assert.That(Raw(chunks[0]), Is.EqualTo(""));
                Assert.That(Raw(chunks[1]), Is.EqualTo("00000"));
                Assert.That(Raw(chunks[2]), Is.EqualTo("1111122222"));
                Assert.That(Raw(chunks[3]), Is.EqualTo("333334444455555"));
                Assert.That(Raw(chunks[4]), Is.EqualTo("66666777778888899999"));

#if DEBUG
                // can fit 15 in each, dropping one byte on the floor
                Assert.That(BufferWriter<byte>.LiveSegmentCount, Is.EqualTo(4));
#endif

                for (int i = 0; i < chunks.Length; i++) _logger.Log($"chunk {i}: {GetState(chunks[i])}");
                for (int i = 0; i < chunks.Length; i++) chunks[i].Dispose();
                for (int i = 0; i < chunks.Length; i++) _logger.Log($"chunk {i}: {GetState(chunks[i])}");
            }
#if DEBUG
            // all dead now
            Assert.That(BufferWriter<byte>.LiveSegmentCount, Is.EqualTo(0));
#endif


        }
        */

        [Test]
        public void BufferWriterReturnsMemoryToPool()
        {
#pragma warning disable IDE0063 // this would break the "all dead now" test
            using (var bw = BufferWriter<byte>.Create(blockSize: 1 << 8))
#pragma warning restore IDE0063
            {
                var span = bw.GetSpan(1);
                span[0] = 123;
                span[128] = 210;
                bw.Advance((1 << 8));
                bw.Flush().Dispose();

                // Release the head off the buffer.
                bw.GetSpan(1);

                var rent = ArrayPool<byte>.Shared.Rent(1 << 8);
                Assert.That(rent[0], Is.EqualTo(123));
                Assert.That(rent[128], Is.EqualTo(210));
            }
        }

        static string GetState(ReadOnlySequence<byte> ros)
        {
            var start = ros.Start;
            var node = start.GetObject() as BufferWriter<byte>.RefCountedSegment;
            long len = ros.Length + start.GetInteger();


            var sb = new StringBuilder();
            sb.Append($"{start.TryGetOffset()}-{ros.End.TryGetOffset()}; counts: ");
            while (node is not null & len > 0)
            {
                sb.Append("[").Append(node!.RunningIndex).Append(',').Append(node.RunningIndex + node.Length).Append("):").Append(node.RefCount).Append(' ');
                len -= node.Length;
                node = (BufferWriter<byte>.RefCountedSegment)node.Next;
            }
            return sb.ToString();
        }

        [Test]
        public void CanAllocateSequences()
        {
            using var bw = BufferWriter<byte>.Create(blockSize: 16);
            _logger.Log(bw.GetState());
            Assert.That(bw.Length, Is.EqualTo(0));

            var seq = bw.GetSequence(70);
            Assert.That(seq.Length, Is.EqualTo(80));
            _logger.Log(bw.GetState());
            Assert.That(bw.Length, Is.EqualTo(0));

            bw.Advance(40);
            _logger.Log(bw.GetState());
            Assert.That(bw.Length, Is.EqualTo(40));

            for (int i = 1; i <= 5; i++)
            {
                _logger.Log($"Leasing span {i}... {bw.GetState()}");
                bw.GetSpan(8);
                bw.Advance(5);
                Assert.That(bw.Length, Is.EqualTo(40 + (5 * i)));
            }
            _logger.Log(bw.GetState());

            Assert.That(bw.Length, Is.EqualTo(65));
            using (var ros = bw.Flush())
            {
                Assert.That(ros.Value.Length, Is.EqualTo(65));
            }
            Assert.That(bw.Length, Is.EqualTo(0));
        }
    }
}
