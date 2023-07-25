using System.Buffers;
using System.Diagnostics;
using System.Net.Sockets;
using MemoryPack;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using NexNet.Cache;
using NexNet.IntegrationTests.TestInterfaces;
using NexNet.Internals;
using NexNet.Internals.Pipes;
using NexNet.Invocation;
using NexNet.Messages;
using NexNet.Transports;
using NUnit.Framework;
#pragma warning disable VSTHRD200

namespace NexNet.IntegrationTests;

internal class NexusDuplexPipeWriterTests
{
    internal class MessengerAndDisconnectorStub : IMessengerAndDisconnector
    {

        public Func<MessageType, ReadOnlySequence<byte>, CancellationToken, ValueTask> OnSendHeaderWithBody = 
            (type, sequence, arg3) => ValueTask.CompletedTask;

        public Func<MessageType, ReadOnlyMemory<byte>?, ReadOnlySequence<byte>, CancellationToken, ValueTask> OnSendCustomHeaderWithBody =
            (type, memory, arg3, arg4) => ValueTask.CompletedTask;

        public ValueTask SendMessage<TMessage>(TMessage body, CancellationToken cancellationToken = default) where TMessage : IMessageBase
        {
            return default;
        }

        public ValueTask SendHeaderWithBody(MessageType type, ReadOnlySequence<byte> body, CancellationToken cancellationToken = default)
        {
            return OnSendHeaderWithBody.Invoke(type, body, cancellationToken);
        }

        public ValueTask SendHeader(MessageType type, CancellationToken cancellationToken = default)
        {
            return default;
        }

        public ValueTask SendHeaderWithBody(MessageType type, ReadOnlyMemory<byte>? messageHeader, ReadOnlySequence<byte> body,
            CancellationToken cancellationToken = default)
        {
            return OnSendCustomHeaderWithBody.Invoke(type, messageHeader, body, cancellationToken);
        }

        public Task DisconnectAsync(DisconnectReason reason)
        {
            return Task.CompletedTask;
        }
    }


    private NexusPipeWriter CreateWriter(MessengerAndDisconnectorStub? messenger = null, IPipeStateManager? stateManager = null)
    {
        stateManager ??= new PipeStateManagerStub();
        messenger ??= new MessengerAndDisconnectorStub();
        var writer = new NexusPipeWriter(stateManager);
        writer.Setup(null,
            messenger,
            true,
            1024);
        return writer;
    }

    [Test]
    public async Task WriterSends()
    {
        var tcs = new TaskCompletionSource();
        var simpleData = new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 };
        var messenger = new MessengerAndDisconnectorStub();
        var writer = CreateWriter(messenger);

        messenger.OnSendCustomHeaderWithBody = (type, header, body, token) =>
        {
            Assert.AreEqual(simpleData, body.ToArray());
            tcs.SetResult();
            return default;
        };

        await writer.WriteAsync(simpleData, CancellationToken.None).AsTask().Timeout(1);
        await tcs.Task.Timeout(1);
    }

    [Test]
    public async Task WriterChunks()
    {
        var tcs = new TaskCompletionSource();
        var simpleData = new byte[512 * 3];
        var messenger = new MessengerAndDisconnectorStub();
        var writer = CreateWriter(messenger);
        writer.Setup(null,
            messenger,
            true,
            1024);
        int invocations = 0;

        messenger.OnSendCustomHeaderWithBody = (type, header, body, token) =>
        {
            if(++invocations == 2)
                tcs.SetResult();

            return default;
        };

        await writer.WriteAsync(simpleData, CancellationToken.None).AsTask().Timeout(1);
        await tcs.Task.Timeout(1);
    }

    [Test]
    public async Task WriterPausesWriting()
    {
        var tcs = new TaskCompletionSource();
        var simpleData = new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 };
        var writer = CreateWriter();
        writer.PauseWriting = true;
        await writer.WriteAsync(simpleData, CancellationToken.None).AsTask().AssertTimeout(.1);
    }

    [Test]
    public async Task WriterResumesWriting()
    {
        var tcs = new TaskCompletionSource();
        var simpleData = new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 };
        var writer = CreateWriter();
        writer.PauseWriting = true;

        _ = Task.Run(async () =>
        {
            await Task.Delay(100);
            writer.PauseWriting = false;
        });

        await writer.WriteAsync(simpleData, CancellationToken.None).AsTask().Timeout(1);
    }

}
