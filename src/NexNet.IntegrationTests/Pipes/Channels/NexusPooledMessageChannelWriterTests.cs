using System.Buffers;
using MemoryPack;
using NexNet.Internals;
using NexNet.Internals.Pipelines.Buffers;
using NexNet.Messages;
using NexNet.Pipes;
using NexNet.Pipes.Channels;
using NUnit.Framework;

namespace NexNet.IntegrationTests.Pipes.Channels;

internal class NexusPooledMessageChannelWriterTests
{
    [Test]
    public async Task WritesData()
    {
        var messenger = new DummySessionMessenger();
        var nexusPipeWriter = new NexusPipeWriter(new DummyPipeStateManager(), null, messenger, true, ushort.MaxValue);
        var writer = new NexusPooledMessageChannelWriter<StandAloneMessage>(nexusPipeWriter);
        var bufferWriter = BufferWriter<byte>.Create();
        messenger.OnMessageSent = (type, header, body) =>
        {
            bufferWriter.Write(body.ToArray());
            return ValueTask.CompletedTask;
        };
        var sendMessage = StandAloneMessage.Rent().Randomize();
        await writer.WriteAsync(sendMessage).Timeout(1);

        var message = MemoryPackSerializer.Deserialize<StandAloneMessage>(bufferWriter.GetBuffer());

        Assert.That(message, Is.EqualTo(sendMessage));
    }

    [Test]
    public async Task WritesDataWithPartialFlush()
    {
        var messenger = new DummySessionMessenger();
        var nexusPipeWriter = new NexusPipeWriter(new DummyPipeStateManager(), null, messenger, true, 1);
        var writer = new NexusChannelWriter<StandAloneMessage>(nexusPipeWriter);
        var bufferWriter = BufferWriter<byte>.Create();
        
        var sendMessage = StandAloneMessage.Rent().Randomize();
        messenger.OnMessageSent = (type, header, body) =>
        {
            bufferWriter.Write(body.ToArray());
            return ValueTask.CompletedTask;
        };

        await writer.WriteAsync(sendMessage).Timeout(1);

        var message = MemoryPackSerializer.Deserialize<StandAloneMessage>(bufferWriter.GetBuffer());

        Assert.That(message, Is.EqualTo(sendMessage));
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
