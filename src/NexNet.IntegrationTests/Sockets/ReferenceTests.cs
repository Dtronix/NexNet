using NexNet.Internals.Pipelines.Arenas;
using NUnit.Framework;

namespace NexNet.IntegrationTests.Sockets
{
    [TestFixture]
    public class ReferenceTests
    {
        [Test]
        public void ArrayReferenceWorks()
        {
            var arr = "abcde".ToArray();
            var r = new Reference<char>(arr, 2);

            Assert.That(r.Value, Is.EqualTo('c'));
            Assert.That((char)r, Is.EqualTo('c'));
            r.Value = 'q';
            Assert.That(arr[2], Is.EqualTo('q'));
            Assert.That(new string(arr), Is.EqualTo("abqde"));
        }
    }
}
