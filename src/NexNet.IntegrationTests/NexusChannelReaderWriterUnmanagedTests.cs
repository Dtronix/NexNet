using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using NexNet.Internals;
using NexNet.Internals.Pipes;
using NexNet.Messages;
using NUnit.Framework;
using Pipelines.Sockets.Unofficial.Arenas;
using Pipelines.Sockets.Unofficial.Buffers;

namespace NexNet.IntegrationTests;

internal class NexusChannelReaderWriterUnmanagedTests
{
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
        public Func<MessageType, ReadOnlyMemory<byte>?, ReadOnlySequence<byte>, ValueTask> OnMessageSent { get; set; }
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
            if(OnMessageSent == null)
                throw new InvalidOperationException("No handler for OnMessageSent");

            return OnMessageSent.Invoke(type, messageHeader, body);
        }

        public Task DisconnectAsync(DisconnectReason reason)
        {
            throw new NotImplementedException();
        }
    }

    private (NexusChannelWriterUnmanaged<T>, NexusChannelReaderUnmanaged<T>) GetReaderWriter<T>()
        where T : unmanaged
    {
        var nexusPipeWriter = new NexusPipeWriter(new DummyPipeStateManager());
        var nexusPipeReader = new NexusPipeReader(new DummyPipeStateManager());
        var messenger = new DummySessionMessenger()
        {
            OnMessageSent = async (type, memory, arg3) =>
            {
                await nexusPipeReader.BufferData(arg3);
            }
        };
        nexusPipeWriter.Setup(new ConsoleLogger(), messenger, true, ushort.MaxValue);

        var writer = new NexusChannelWriterUnmanaged<T>(nexusPipeWriter);
        var reader = new NexusChannelReaderUnmanaged<T>(nexusPipeReader);

        return (writer, reader);
    }

    [Test]
    public async Task WritesAndReadsData()
    {
        var (writer, reader) = GetReaderWriter<long>();
        var value = 123456789L;
        await writer.WriteAsync(value);
        Assert.AreEqual(value, (await reader.ReadAsync()).Single());
    }

    [Test]
    public async Task WritesAndReadsMultipleData()
    {
        var (writer, reader) = GetReaderWriter<long>();
        var iterations = 1000;
        for (int i = 0; i < iterations; i++)
        {
            await writer.WriteAsync(i);
        }

        var count = 0L;
        foreach (var l in await reader.ReadAsync())
        {
            Assert.AreEqual(count++, l);
        }

        Assert.AreEqual(iterations, count);
    }
}
