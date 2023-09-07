using System.Buffers;
using MemoryPack;
using NexNet.Internals;
using NexNet.Messages;
using NexNet.Pipes;
using NUnit.Framework;
using Pipelines.Sockets.Unofficial.Buffers;

namespace NexNet.IntegrationTests.Pipes;

internal class NexusChannelWriterTests
{
    [Test]
    public async Task WritesData()
    {
        var nexusPipeWriter = new NexusPipeWriter(new DummyPipeStateManager());
        var messenger = new DummySessionMessenger();
        nexusPipeWriter.Setup(new ConsoleLogger(), messenger, true, ushort.MaxValue);
        var writer = new NexusChannelWriter<ComplexMessage>(nexusPipeWriter);
        var bufferWriter = BufferWriter<byte>.Create();
        var baseObject = ComplexMessage.Random();
        messenger.OnMessageSent = (type, header, body) =>
        {
            bufferWriter.Write(body.ToArray());
            return ValueTask.CompletedTask;
        };

        await writer.WriteAsync(baseObject).Timeout(1);

        var message = MemoryPackSerializer.Deserialize<ComplexMessage>(bufferWriter.GetBuffer());

        Assert.AreEqual(baseObject, message);
    }

    [Test]
    public async Task WritesDataWithPartialFlush()
    {
        var nexusPipeWriter = new NexusPipeWriter(new DummyPipeStateManager());
        var messenger = new DummySessionMessenger();
        nexusPipeWriter.Setup(new ConsoleLogger(), messenger, true, 1);
        var writer = new NexusChannelWriter<ComplexMessage>(nexusPipeWriter);
        var bufferWriter = BufferWriter<byte>.Create();
        var baseObject = ComplexMessage.Random();
        messenger.OnMessageSent = (type, header, body) =>
        {
            bufferWriter.Write(body.ToArray());
            return ValueTask.CompletedTask;
        };

        await writer.WriteAsync(baseObject).Timeout(1);

        var message = MemoryPackSerializer.Deserialize<ComplexMessage>(bufferWriter.GetBuffer());

        Assert.AreEqual(baseObject, message);
    }

    [Test]
    public async Task WritesCancels()
    {
        var nexusPipeWriter = new NexusPipeWriter(new DummyPipeStateManager())
        {
            PauseWriting = true
        };

        var writer = new NexusChannelWriter<ComplexMessage>(nexusPipeWriter);
        nexusPipeWriter.Setup(new ConsoleLogger(), new DummySessionMessenger(), true, ushort.MaxValue);

        var cts = new CancellationTokenSource(100);
        var writeResult = await writer.WriteAsync(ComplexMessage.Random(), cts.Token).Timeout(1);

        Assert.IsFalse(writeResult);
        Assert.IsFalse(writer.IsComplete);
    }

    [Test]
    public async Task WritesCancelsImmediately()
    {
        var nexusPipeWriter = new NexusPipeWriter(new DummyPipeStateManager())
        {
            PauseWriting = true
        };

        var writer = new NexusChannelWriter<ComplexMessage>(nexusPipeWriter);
        nexusPipeWriter.Setup(new ConsoleLogger(), new DummySessionMessenger(), true, ushort.MaxValue);

        var cts = new CancellationTokenSource();
        cts.Cancel();
        var writeResult = await writer.WriteAsync(ComplexMessage.Random(), cts.Token).Timeout(1);

        Assert.IsFalse(writeResult);
        Assert.IsFalse(writer.IsComplete);
    }

    private class DummyPipeStateManager : IPipeStateManager
    {
        public ushort Id { get; } = 0;
        public ValueTask NotifyState()
        {
            return default;
        }

        public bool UpdateState(NexusDuplexPipe.State updatedState, bool remove = false)
        {
            CurrentState |= updatedState;
            return true;
        }

        public NexusDuplexPipe.State CurrentState { get; private set; } = NexusDuplexPipe.State.Ready;
    }

    private class DummySessionMessenger : ISessionMessenger
    {
        public Func<MessageType, ReadOnlyMemory<byte>?, ReadOnlySequence<byte>, ValueTask> OnMessageSent { get; set; } = null!;
        public ValueTask SendMessage<TMessage>(TMessage body, CancellationToken cancellationToken = default) where TMessage : IMessageBase
        {
            throw new NotImplementedException();
        }

        public ValueTask SendHeaderWithBody(MessageType type, ReadOnlySequence<byte> body, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public ValueTask SendHeader(MessageType type, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public ValueTask SendHeaderWithBody(MessageType type, ReadOnlyMemory<byte>? messageHeader, ReadOnlySequence<byte> body,
            CancellationToken cancellationToken = default)
        {
            if (OnMessageSent == null)
                throw new InvalidOperationException("No handler for OnMessageSent");

            return OnMessageSent.Invoke(type, messageHeader, body);
        }

        public Task DisconnectAsync(DisconnectReason reason)
        {
            throw new NotImplementedException();
        }
    }
}
