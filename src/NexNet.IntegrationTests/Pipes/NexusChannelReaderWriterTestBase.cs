﻿using System.Buffers;
using NexNet.Internals;
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

    protected (NexusPipeWriter, NexusPipeReader) GetConnectedPipeReaderWriter()
    {
        NexusPipeWriter nexusPipeWriter = null!;
        NexusPipeReader nexusPipeReader = null!;

        var messenger = new DummySessionMessenger()
        {
            OnMessageSent = async (type, memory, arg3) =>
            {
                await nexusPipeReader!.BufferData(arg3).Timeout(1);
            }
        };
        nexusPipeWriter = new NexusPipeWriter(
            new DummyPipeStateManager(),
            new ConsoleLogger(),
            messenger,
            true,
            ushort.MaxValue);
        nexusPipeReader = new NexusPipeReader(
            new DummyPipeStateManager(),
            new ConsoleLogger(),
            true,
            ushort.MaxValue,
            0,
            0);

        return (nexusPipeWriter, nexusPipeReader);
    }
}
