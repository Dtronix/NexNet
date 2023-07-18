using System.Buffers;
using NexNet.Internals;
using NUnit.Framework;

namespace NexNet.IntegrationTests
{
    public class PipeReaderImplTests
    {
        private NexusDuplexPipe.PipeReaderImpl _pipeReader;

        [SetUp]
        public void SetUp()
        {
            _pipeReader = new NexusDuplexPipe.PipeReaderImpl();
        }

        [Test]
        public void BufferData_CopiesDataToBuffer()
        {
            var buffer = new byte[] { 1, 2, 3 };
            var data = new ReadOnlySequence<byte>(buffer);
            _pipeReader.BufferData(data);

            Assert.That(_pipeReader.TryRead(out var result), Is.True);
            Assert.That(result.Buffer.ToArray(), Is.EqualTo(buffer));
        }

        [Test]
        public void BufferData_TriggersTryRead()
        {
            var buffer = new byte[] { 1, 2, 3 };
            var data = new ReadOnlySequence<byte>(buffer);
            _pipeReader.BufferData(data);

            Assert.That(_pipeReader.TryRead(out var result), Is.True);
        }

        [Test]
        public void ReadAsync_CompletesWhenCancelling()
        {
            var source = new CancellationTokenSource();
            var readTask = _pipeReader.ReadAsync(source.Token);
            source.Cancel();
            
            Assert.That(async () => await readTask, Throws.Nothing);
        }

    }
}
