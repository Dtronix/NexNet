using NexNet.Collections;
using NexNet.Collections.Lists;
using NexNet.Pipes;
using NexNet.Pipes.Broadcast;

namespace NexNet.IntegrationTests.Collections;

/// <summary>
/// The collection pipe closes while EnableAsync is still setting the run up (S21): the server stopped, or the pipe
/// closed, right after the server opened it.
/// </summary>
internal class NexusCollectionEnableCloseRaceTests : BaseTests
{
    [Test]
    [Transports(Type.Uds, Type.Tcp)]
    public async Task PipeClosedDuringEnable_EnableReturnsFalse_AndFreesThePipe(Type type)
    {
        var server = CreateServer(CreateServerConfig(type), null);
        await server.StartAsync().Timeout(TestTimeouts.Connect);
        var (client, _) = CreateClient(CreateClientConfig(type));
        await client.ConnectAsync().Timeout(TestTimeouts.Connect);

        var list = client.Proxy.IntListBi;
        var hooked = (NexusBroadcastClient<INexusCollectionListMessage>)(object)list;

        // The pipe closes and the run's disconnect cleanup finishes before the rest of the enable runs.
        hooked.EnableTestHook = async pipe =>
        {
            await ((NexusDuplexPipe)pipe).CompleteAsync();
            await list.DisconnectedTask.Timeout(5);
        };

        var nres = NullReferenceCounter.Start();
        bool enabled;
        try
        {
            enabled = await list.EnableAsync().Timeout(5);
        }
        finally
        {
            nres.Dispose();
            hooked.EnableTestHook = null;
        }

        await Assert.That(enabled).IsFalse();
        await Assert.That(nres.Count).IsZero();

        // The run's reader returns the pipe, so its slot is freed once the server confirmed the close.
        var pipes = client.Session!.PipeManager;
        await WaitUntil(() => pipes.OpenPipes == 0, TimeSpan.FromSeconds(5));
        await Assert.That(pipes.OpenPipes).IsZero();

        await Assert.That(await list.EnableAsync().Timeout(5)).IsTrue();
        await list.DisableAsync().Timeout(5);
    }

    [Test]
    [Transports(Type.Uds, Type.Tcp)]
    public async Task ServerStopsDuringEnable_EnableDoesNotThrowNullReference(Type type)
    {
        var server = CreateServer(CreateServerConfig(type), null);
        await server.StartAsync().Timeout(TestTimeouts.Connect);
        var (client, _) = CreateClient(CreateClientConfig(type));
        await client.ConnectAsync().Timeout(TestTimeouts.Connect);

        var list = client.Proxy.IntListBi;
        var hooked = (NexusBroadcastClient<INexusCollectionListMessage>)(object)list;

        hooked.EnableTestHook = async _ =>
        {
            await server.StopAsync().Timeout(5);
            await list.DisconnectedTask.Timeout(5);
        };

        var nres = NullReferenceCounter.Start();
        Exception? error = null;
        var enabled = false;
        try
        {
            enabled = await list.EnableAsync().Timeout(5);
        }
        catch (Exception e)
        {
            error = e;
        }
        finally
        {
            nres.Dispose();
            hooked.EnableTestHook = null;
        }

        using (Assert.Multiple())
        {
            await Assert.That(error).IsNull();
            await Assert.That(enabled).IsFalse();
            await Assert.That(nres.Count).IsZero();
        }
    }

    /// <summary>
    /// Counts the first-chance NullReferenceExceptions raised by the current test's own async flow: the test body and
    /// the client, server and pipe work it starts, which inherit its execution context. FirstChanceException is
    /// process-wide, so counting every NullReferenceException also counted those of tests running in parallel
    /// (NexusClientPoolTests.Pool_RentClientAsync_HandlesFactoryReturningNull throws one on purpose).
    /// </summary>
    private sealed class NullReferenceCounter : IDisposable
    {
        private static readonly AsyncLocal<NullReferenceCounter?> Current = new();
        private int _count;

        public int Count => Volatile.Read(ref _count);

        private NullReferenceCounter()
        {
        }

        public static NullReferenceCounter Start()
        {
            var counter = new NullReferenceCounter();
            Current.Value = counter;
            AppDomain.CurrentDomain.FirstChanceException += counter.OnFirstChanceException;
            return counter;
        }

        private void OnFirstChanceException(object? sender, System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs e)
        {
            if (e.Exception is NullReferenceException && ReferenceEquals(Current.Value, this))
                Interlocked.Increment(ref _count);
        }

        public void Dispose() => AppDomain.CurrentDomain.FirstChanceException -= OnFirstChanceException;
    }

    private static async Task WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(10);
    }
}
