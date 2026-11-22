using NexNet.IntegrationTests.SessionManagement;
using NexNet.Pipes;
using NexNet.Pools;

namespace NexNet.IntegrationTests.Pipes;

/// <summary>
/// A pipe whose state message fails to send closes through the normal completion path (S21).
/// </summary>
internal class NexusDuplexPipeNotifyStateTests
{
    private static NexusDuplexPipe CreatePipe(MockNexusSession session)
    {
        var manager = new NexusPipeManager();
        manager.Setup(session);
        session.PipeManager = manager;
        return new NexusDuplexPipe(NexusPipeManager.ComposeId(1, 1), 1, session);
    }

    [Test]
    public async Task SendFailure_UnblocksWaitingReader_AndCompletesPipe()
    {
        var session = new MockNexusSession { PoolManager = new PoolManager() };
        var pipe = CreatePipe(session);
        pipe.UpdateState(NexusDuplexPipe.State.Ready);

        var read = pipe.Input.ReadAsync().AsTask();
        await Task.Delay(50);
        await Assert.That(read.IsCompleted).IsFalse();

        session.ShouldFailSend = true;
        await pipe.NotifyState();

        var result = await read.Timeout(5);
        using (Assert.Multiple())
        {
            await Assert.That(result.IsCompleted).IsTrue();
            await Assert.That(pipe.CompleteTask.IsCompletedSuccessfully).IsTrue();
            await Assert.That(pipe.CurrentState).IsEqualTo(NexusDuplexPipe.State.Complete);
        }
    }

    [Test]
    public async Task SendReadyFailure_CompletesReaderWriterAndCompleteTask()
    {
        var session = new MockNexusSession { PoolManager = new PoolManager(), ShouldFailSend = true };
        var pipe = CreatePipe(session);

        await pipe.SendReady();

        var read = await pipe.Input.ReadAsync().AsTask().Timeout(5);
        var flush = await pipe.Output.WriteAsync(new byte[] { 1 }).AsTask().Timeout(5);
        using (Assert.Multiple())
        {
            await Assert.That(read.IsCompleted).IsTrue();
            await Assert.That(flush.IsCompleted).IsTrue();
            await Assert.That(pipe.CompleteTask.IsCompletedSuccessfully).IsTrue();
        }
    }
}
