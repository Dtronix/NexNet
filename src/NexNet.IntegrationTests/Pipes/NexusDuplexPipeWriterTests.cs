using System.Buffers;
using System.Runtime.CompilerServices;
using NexNet.Internals;
using NexNet.Messages;
using NexNet.Pipes;
using NUnit.Framework;
#pragma warning disable VSTHRD200

namespace NexNet.IntegrationTests.Pipes;

internal class NexusDuplexPipeWriterTests
{
    internal class SessionMessengerStub : ISessionMessenger
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

        public Task DisconnectAsync(DisconnectReason reason,
            [CallerFilePath]string? filePath = null, 
            [CallerLineNumber] int? lineNumber = null)
        {
            return Task.CompletedTask;
        }
    }


    private NexusPipeWriter CreateWriter(SessionMessengerStub? messenger = null, IPipeStateManager? stateManager = null, long credit = TestPipes.LargeWindow)
    {
        stateManager ??= new PipeStateManagerStub();
        messenger ??= new SessionMessengerStub();
        var writer = new NexusPipeWriter(
            stateManager,
            null,
            messenger,
            true,
            1024);
        if (credit > 0)
            writer.AddCredit(credit);
        return writer;
    }

    [Test]
    public async Task WriterSends()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var simpleData = new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 };
        var messenger = new SessionMessengerStub();
        var writer = CreateWriter(messenger);

        messenger.OnSendCustomHeaderWithBody = (type, header, body, token) =>
        {
            Assert.That(body.ToArray(), Is.EqualTo(simpleData));
            tcs.SetResult();
            return default;
        };

        await writer.WriteAsync(simpleData, CancellationToken.None).Timeout(1);
        await tcs.Task.Timeout(1);
    }

    [Test]
    public async Task WriterChunks()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var simpleData = new byte[512 * 3];
        var messenger = new SessionMessengerStub();
        var writer = CreateWriter(messenger);
        var invocations = 0;

        messenger.OnSendCustomHeaderWithBody = (type, header, body, token) =>
        {
            if (Interlocked.Increment(ref invocations) == 2)
                tcs.SetResult();

            return default;
        };

        await writer.WriteAsync(simpleData, CancellationToken.None).Timeout(1);
        await tcs.Task.Timeout(1);
    }

    [Test]
    public async Task WriterWaitsWithoutCredit()
    {
        var simpleData = new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 };
        var writer = CreateWriter(credit: 0);
        await writer.WriteAsync(simpleData, CancellationToken.None).AsTask().AssertTimeout(.1);
    }

    [Test]
    public async Task WriterResumesWhenCreditIsGranted()
    {
        var simpleData = new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 };
        var writer = CreateWriter(credit: 0);

        _ = Task.Run(async () =>
        {
            await Task.Delay(100);
            writer.AddCredit(simpleData.Length);
        });

        await writer.WriteAsync(simpleData, CancellationToken.None).Timeout(1);
    }

    [Test]
    public async Task WriterSendsOnlyWhatTheCreditAllows()
    {
        var messenger = new SessionMessengerStub();
        var sent = new List<byte>();
        messenger.OnSendCustomHeaderWithBody = (_, _, body, _) =>
        {
            lock (sent)
                sent.AddRange(body.ToArray());
            return default;
        };
        var writer = CreateWriter(messenger, credit: 4);

        var write = writer.WriteAsync(new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 }, CancellationToken.None).AsTask();
        await Task.Delay(100);
        lock (sent)
            Assert.That(sent, Is.EqualTo(new byte[] { 0, 1, 2, 3 }));
        Assert.That(write.IsCompleted, Is.False);

        writer.AddCredit(6);
        await write.Timeout(1);
        Assert.That(sent, Is.EqualTo(new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 }));
        Assert.That(writer.Credit, Is.Zero);
    }

    [Test]
    public void WriterRejectsCreditBeyondTheMaximum()
    {
        var writer = CreateWriter(credit: 0);
        Assert.That(writer.AddCredit(NexusPipeWriter.MaxCredit), Is.True);
        Assert.That(writer.AddCredit(1), Is.False);
    }

}
