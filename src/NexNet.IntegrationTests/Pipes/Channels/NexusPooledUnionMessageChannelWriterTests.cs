using System.Buffers;
using MemoryPack;
using NexNet.Internals;
using NexNet.Internals.Pipelines.Buffers;
using NexNet.Messages;
using NexNet.Pipes;
using NexNet.Pipes.Channels;
using NUnit.Framework;

namespace NexNet.IntegrationTests.Pipes.Channels;

internal class NexusPooledUnionMessageChannelWriterTests
{
    [Test]
    public async Task WritesData()
    {
        var messenger = new DummySessionMessenger();
        var nexusPipeWriter = new NexusPipeWriter(new DummyPipeStateManager(), null, messenger, true, ushort.MaxValue);
        var writer = new NexusPooledUnionMessageChannelWriter<NetworkMessageUnion>(nexusPipeWriter);
        var bufferWriter = BufferWriter<byte>.Create();
        messenger.OnMessageSent = (type, header, body) =>
        {
            bufferWriter.Write(body.ToArray());
            return ValueTask.CompletedTask;
        };
        
        var sendMessage = LoginMessage.Rent().Randomize();
        await writer.WriteAsync(sendMessage).Timeout(1);

        var id = NexusPooledMessageUnionRegistry<NetworkMessageUnion>.GetMessageType<LoginMessage>();
        var bufferedData = bufferWriter.GetBuffer();
        
        Assert.That(bufferedData.FirstSpan[0], Is.EqualTo(id));
        
        // Slice off the message type header.
        var remainingBuffer = bufferedData.Slice(1);

        var message = MemoryPackSerializer.Deserialize<LoginMessage>(remainingBuffer);

        Assert.That(message, Is.EqualTo(sendMessage));
    }

    [Test]
    public async Task WritesDataWithPartialFlush()
    {
        var messenger = new DummySessionMessenger();
        var nexusPipeWriter = new NexusPipeWriter(new DummyPipeStateManager(), null, messenger, true, 1);
        var writer = new NexusPooledUnionMessageChannelWriter<NetworkMessageUnion>(nexusPipeWriter);
        var bufferWriter = BufferWriter<byte>.Create();
        
        messenger.OnMessageSent = (type, header, body) =>
        {
            bufferWriter.Write(body.ToArray());
            return ValueTask.CompletedTask;
        };

        var sendMessage = LoginMessage.Rent().Randomize();
        await writer.WriteAsync(sendMessage).Timeout(1);

        var id = NexusPooledMessageUnionRegistry<NetworkMessageUnion>.GetMessageType<LoginMessage>();
        var bufferedData = bufferWriter.GetBuffer();
        
        Assert.That(bufferedData.FirstSpan[0], Is.EqualTo(id));
        
        // Slice off the message type header.
        var remainingBuffer = bufferedData.Slice(1);

        var message = MemoryPackSerializer.Deserialize<LoginMessage>(remainingBuffer);

        Assert.That(message, Is.EqualTo(sendMessage));
    }
    
    [Test]
    public async Task WritesMultipleUnionTypes()
    {
        var messenger = new DummySessionMessenger();
        var nexusPipeWriter = new NexusPipeWriter(new DummyPipeStateManager(), null, messenger, true, ushort.MaxValue);
        var writer = new NexusPooledUnionMessageChannelWriter<NetworkMessageUnion>(nexusPipeWriter);
        var bufferWriter = BufferWriter<byte>.Create();
        messenger.OnMessageSent = (type, header, body) =>
        {
            bufferWriter.Write(body.ToArray());
            return ValueTask.CompletedTask;
        };
        
        var sendMessage1 = LoginMessage.Rent().Randomize();
        var sendMessage2 = ChatMessage.Rent().Randomize();
        var sendMessage3 = DisconnectMessage.Rent().Randomize();
        
        await writer.WriteAsync(sendMessage1).Timeout(1);
        await writer.WriteAsync(sendMessage2).Timeout(1);
        await writer.WriteAsync(sendMessage3).Timeout(1);

        var ids = new[]
        {
            NexusPooledMessageUnionRegistry<NetworkMessageUnion>.GetMessageType<LoginMessage>(),
            NexusPooledMessageUnionRegistry<NetworkMessageUnion>.GetMessageType<ChatMessage>(),
            NexusPooledMessageUnionRegistry<NetworkMessageUnion>.GetMessageType<DisconnectMessage>()
        };
        
        var bufferedData = bufferWriter.GetBuffer();

        using var readerState = MemoryPackReaderOptionalStatePool.Rent(MemoryPackSerializerOptions.Default);
        var reader = new MemoryPackReader(bufferedData, readerState);
        
        Assert.That(reader.ReadValue<byte>(), Is.EqualTo(ids[0]));
        var message1 = reader.ReadValue<LoginMessage>();
        Assert.That(sendMessage1, Is.EqualTo(message1));
            
        Assert.That(reader.ReadValue<byte>(), Is.EqualTo(ids[1]));
        var message2 = reader.ReadValue<ChatMessage>();
        Assert.That(sendMessage2, Is.EqualTo(message2));
        
        Assert.That(reader.ReadValue<byte>(), Is.EqualTo(ids[2]));
        var message3 = reader.ReadValue<DisconnectMessage>();
        Assert.That(sendMessage3, Is.EqualTo(message3));
    }
    
        [Test]
    public async Task WritesMultipleUnionTypes_CastedToUnionType()
    {
        var messenger = new DummySessionMessenger();
        var nexusPipeWriter = new NexusPipeWriter(new DummyPipeStateManager(), null, messenger, true, ushort.MaxValue);
        var writer = new NexusPooledUnionMessageChannelWriter<NetworkMessageUnion>(nexusPipeWriter);
        var bufferWriter = BufferWriter<byte>.Create();
        messenger.OnMessageSent = (type, header, body) =>
        {
            bufferWriter.Write(body.ToArray());
            return ValueTask.CompletedTask;
        };
        
        NetworkMessageUnion sendMessage1 = LoginMessage.Rent().Randomize();
        NetworkMessageUnion sendMessage2 = ChatMessage.Rent().Randomize();
        NetworkMessageUnion sendMessage3 = DisconnectMessage.Rent().Randomize();
        
        await writer.WriteAsync(sendMessage1).Timeout(1);
        await writer.WriteAsync(sendMessage2).Timeout(1);
        await writer.WriteAsync(sendMessage3).Timeout(1);

        var ids = new[]
        {
            NexusPooledMessageUnionRegistry<NetworkMessageUnion>.GetMessageType<LoginMessage>(),
            NexusPooledMessageUnionRegistry<NetworkMessageUnion>.GetMessageType<ChatMessage>(),
            NexusPooledMessageUnionRegistry<NetworkMessageUnion>.GetMessageType<DisconnectMessage>()
        };
        
        var bufferedData = bufferWriter.GetBuffer();

        using var readerState = MemoryPackReaderOptionalStatePool.Rent(MemoryPackSerializerOptions.Default);
        var reader = new MemoryPackReader(bufferedData, readerState);
        
        Assert.That(reader.ReadValue<byte>(), Is.EqualTo(ids[0]));
        var message1 = reader.ReadValue<LoginMessage>();
        Assert.That(sendMessage1, Is.EqualTo(message1));
            
        Assert.That(reader.ReadValue<byte>(), Is.EqualTo(ids[1]));
        var message2 = reader.ReadValue<ChatMessage>();
        Assert.That(sendMessage2, Is.EqualTo(message2));
        
        Assert.That(reader.ReadValue<byte>(), Is.EqualTo(ids[2]));
        var message3 = reader.ReadValue<DisconnectMessage>();
        Assert.That(sendMessage3, Is.EqualTo(message3));
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
