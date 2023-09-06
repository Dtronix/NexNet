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

internal class NexusChannelReaderWriterTests
{
    [Test]
    public async Task WritesAndReadsData()
    {
        var (writer, reader) = GetReaderWriter<ComplexMessage>();
        var value = ComplexMessage.Random();
        await writer.WriteAsync(value);
        var result = await reader.ReadAsync().AsTask().Timeout(1);
        Assert.AreEqual(value, result.Single());
    }

    [Test]
    public async Task WritesAndReadsMultipleData()
    {
        var (writer, reader) = GetReaderWriter<ComplexMessage>();
        var iterations = 1000;
        var value = ComplexMessage.Random();
        for (int i = 0; i < iterations; i++)
        {
            await writer.WriteAsync(value);
        }

        var result = await reader.ReadAsync().AsTask().Timeout(1);
        foreach (var complexMessage in result)
        {
            Assert.AreEqual(value, complexMessage);
        }

        Assert.AreEqual(iterations, result.Count());
    }

    [Test]
    public async Task WritesAndReadsMultipleDataParallel()
    {
        var (writer, reader) = GetReaderWriter<ComplexMessage>();
        var iterations = 100000;
        var value = ComplexMessage.Random();
        var count = 0;
        _ = Task.Run(async () =>
        {
            for (int i = 0; i < iterations; i++)
            {
                await writer.WriteAsync(value);
            }
        });

        await Task.Run(async () =>
        {
            while (true)
            {
                var result = await reader.ReadAsync().AsTask().Timeout(1);
                foreach (var complexMessage in result)
                {
                    Assert.AreEqual(value, complexMessage);
                    count++;
                }

                if (count == iterations)
                {
                    break;
                }
            }
        });

        Assert.AreEqual(iterations, count);

    }
    private (NexusChannelWriter<T>, NexusChannelReader<T>) GetReaderWriter<T>()
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

        var writer = new NexusChannelWriter<T>(nexusPipeWriter);
        var reader = new NexusChannelReader<T>(nexusPipeReader);

        return (writer, reader);
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
        public Func<MessageType, ReadOnlyMemory<byte>?, ReadOnlySequence<byte>, ValueTask> 
            OnMessageSent { get; set; } = null!;
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
