using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using NexNet.Internals;
using NexNet.Messages;
using NexNet.Pipes;
using NUnit.Framework;
using Pipelines.Sockets.Unofficial.Arenas;
using Pipelines.Sockets.Unofficial.Buffers;

namespace NexNet.IntegrationTests;

internal class NexusChannelWriterUnmanagedTests
{
    [TestCase((sbyte)-54)]
    [TestCase((byte)200)]
    [TestCase((short)22584)]
    [TestCase((ushort)62584)]
    [TestCase((int)65122584)]
    [TestCase((uint)616322584)]
    [TestCase((long)92175120571057)]
    [TestCase((ulong)6163225235237523984)]
    [TestCase((char)'n')]
    [TestCase((float)9873571.1922)]
    [TestCase((double)9851512573571.198422)]
    public async Task WritesData<T>(T inputData)
        where T : unmanaged
    {
        var nexusPipeWriter = new NexusPipeWriter(new DummyPipeStateManager());
        var messenger = new DummySessionMessenger();
        nexusPipeWriter.Setup(new ConsoleLogger(), messenger, true, ushort.MaxValue);
        var writer = new NexusChannelWriterUnmanaged<T>(nexusPipeWriter);
        var bufferWriter = BufferWriter<byte>.Create();
        messenger.OnMessageSent = (type, header, body) =>
        {
            bufferWriter.Write(body.ToArray());
            return ValueTask.CompletedTask;
        };

        await writer.WriteAsync(inputData);

        Assert.AreEqual(inputData, Utilities.GetValue<T>(bufferWriter.GetBuffer().ToArray()));
    }

    [TestCase((sbyte)-54)]
    [TestCase((byte)200)]
    [TestCase((short)22584)]
    [TestCase((ushort)62584)]
    [TestCase((int)65122584)]
    [TestCase((uint)616322584)]
    [TestCase((long)92175120571057)]
    [TestCase((ulong)6163225235237523984)]
    [TestCase((char)'n')]
    [TestCase((float)9873571.1922)]
    [TestCase((double)9851512573571.198422)]
    public async Task WritesDataWithPartialFlush<T>(T inputData)
        where T : unmanaged
    {
        var nexusPipeWriter = new NexusPipeWriter(new DummyPipeStateManager());
        var messenger = new DummySessionMessenger();
        var writer = new NexusChannelWriterUnmanaged<T>(nexusPipeWriter);
        var bufferWriter = BufferWriter<byte>.Create();

        nexusPipeWriter.Setup(new ConsoleLogger(), messenger, true, 1);

        messenger.OnMessageSent = (type, header, body) =>
        {
            bufferWriter.Write(body.ToArray());
            return default;
        };

        await writer.WriteAsync(inputData);

        Assert.AreEqual(inputData, Utilities.GetValue<T>(bufferWriter.GetBuffer().ToArray()));
    }

    [Test]
    public async Task WritesCancels()
    {
        var nexusPipeWriter = new NexusPipeWriter(new DummyPipeStateManager())
        {
            PauseWriting = true
        };

        var writer = new NexusChannelWriterUnmanaged<long>(nexusPipeWriter);
        nexusPipeWriter.Setup(new ConsoleLogger(), new DummySessionMessenger(), true, ushort.MaxValue);

        var cts = new CancellationTokenSource(100);
        var writeResult = await writer.WriteAsync(123456789L, cts.Token).AsTask().Timeout(1);

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

        var writer = new NexusChannelWriterUnmanaged<long>(nexusPipeWriter);
        nexusPipeWriter.Setup(new ConsoleLogger(), new DummySessionMessenger(), true, ushort.MaxValue);

        var cts = new CancellationTokenSource();
        cts.Cancel();
        var writeResult = await writer.WriteAsync(123456789L, cts.Token).AsTask().Timeout(1);

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
