using System.Buffers;
using NexNet.Internals;
using NexNet.Logging;
using NexNet.Messages;
using NexNet.Pipes;

namespace NexNet.IntegrationTests.Pipes;

internal class NexusChannelReaderWriterTestBase
{

    protected class DummyPipeStateManager : IPipeStateManager
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

    protected class DummySessionMessenger : ISessionMessenger
    {
        public Func<MessageType, ReadOnlyMemory<byte>?, ReadOnlySequence<byte>, ValueTask>
            OnMessageSent
        { get; set; } = null!;
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

    protected (NexusPipeWriter, NexusPipeReader, DummySessionMessenger) GetConnectedPipeReaderWriter(bool log = false)
    {
        var nexusPipeReader = new NexusPipeReader(
            new DummyPipeStateManager(),
            log ? new ConsoleLogger() : null,
            true,
            ushort.MaxValue,
            0,
            0);

        var messenger = new DummySessionMessenger()
        {
            OnMessageSent = async (type, memory, arg3) =>
            {
                await nexusPipeReader!.BufferData(arg3).Timeout(1);
            }
        };

        var nexusPipeWriter = new NexusPipeWriter(
        new DummyPipeStateManager(),
            log ? new ConsoleLogger() : null,
            messenger,
            true,
            ushort.MaxValue);


        return (nexusPipeWriter, nexusPipeReader, messenger);
    }
}
