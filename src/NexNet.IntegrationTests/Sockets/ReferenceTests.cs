using NexNet.Internals.Pipelines.Arenas;
using System;
using System.Linq;
using NUnit;

namespace NexNet.Internals.Pipelines.Tests
{
    internal class ReferenceTests
    {
        [Fact]
        public void ArrayReferenceWorks()
        {
            var arr = "abcde".ToArray();
            var r = new Reference<char>(arr, 2);

            Assert.Equal('c', r.Value);
            Assert.Equal('c', (char)r);
            r.Value = 'q';
            Assert.Equal('q', arr[2]);
            Assert.Equal("abqde", new string(arr));
        }
    }
}
