using NexNet.Internals.Pipelines.Arenas;
using NUnit.Framework;

namespace NexNet.IntegrationTests.Sockets
{
    [TestFixture]
    public class FromEnumerableTests
    {
        [Test]
        public void FromEnumerableNull()
        {
            IEnumerable<int>? source = null;
            Assert.Throws<ArgumentNullException>(() => source.ToSequence());
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        [TestCase(15)]
        [TestCase(16)]
        [TestCase(17)]
        [TestCase(1023)]
        [TestCase(1024)]
        [TestCase(1025)]
        public void FromEnumerableRange(int count)
        {
            var source = Enumerable.Range(42, count);
            var sequence = source.ToSequence();
            Assert.That(sequence.Length, Is.EqualTo(count));
            for (int i = 0; i < count; i++)
                Assert.That(sequence[i], Is.EqualTo(42 + i));
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        [TestCase(15)]
        [TestCase(16)]
        [TestCase(17)]
        [TestCase(1023)]
        [TestCase(1024)]
        [TestCase(1025)]
        public void FromArray(int count)
        {
            var source = Enumerable.Range(42, count).ToArray();
            var sequence = source.ToSequence();
            Assert.That(sequence.Length, Is.EqualTo(count));
            for (int i = 0; i < count; i++)
            {
                Assert.That(sequence[i], Is.EqualTo(42 + i));
                // check reuses existing data
                Assert.That(sequence.GetReference(i), Is.EqualTo(new Reference<int>(source, i)));
            }
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        [TestCase(15)]
        [TestCase(16)]
        [TestCase(17)]
        [TestCase(1023)]
        [TestCase(1024)]
        [TestCase(1025)]
        public void FromSequenceList(int count)
        {
            var original = new Sequence<int>(Enumerable.Range(42, count).ToArray());
            IEnumerable<int> source = original.ToList();
            var sequence = source.ToSequence();
            Assert.That(sequence.Length, Is.EqualTo(count));
            for (int i = 0; i < count; i++)
            {
                Assert.That(sequence[i], Is.EqualTo(42 + i));
                // check reuses existing data
                Assert.That(sequence.GetReference(i), Is.EqualTo(original.GetReference(i)));
            }
        }
    }
}
