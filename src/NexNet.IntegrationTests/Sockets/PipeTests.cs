using System.IO.Pipelines;
using NexNet.Internals.Pipelines;
using NUnit.Framework;

namespace NexNet.IntegrationTests.Sockets
{
    [TestFixture]
    public class PipeTests
    {
        [Test]
        public async Task PipeLengthWorks()
        {
            var pipe = new Pipe();
            var span = pipe.Writer.GetSpan(42);
            for (int i = 0; i < 42; i++)
                span[i] = (byte)i;
            pipe.Writer.Advance(42);
            await pipe.Writer.FlushAsync(); // this is what changes the length

            Assert.That(SocketConnection.Counters.GetPipeLength(pipe), Is.EqualTo(42));

            Assert.That(pipe.Reader.TryRead(out var result), Is.True);
            Assert.That(result.Buffer.Length, Is.EqualTo(42));
            Assert.That(SocketConnection.Counters.GetPipeLength(pipe), Is.EqualTo(42));

            pipe.Reader.AdvanceTo(result.Buffer.End);
            Assert.That(SocketConnection.Counters.GetPipeLength(pipe), Is.EqualTo(0));
        }
    }
}
