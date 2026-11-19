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
        var nres = 0;
        EventHandler<System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs> onException = (_, e) =>
        {
            if (e.Exception is NullReferenceException)
                Interlocked.Increment(ref nres);
        };

        // The pipe closes and the run's disconnect cleanup finishes before the rest of the enable runs.
        hooked.EnableTestHook = async pipe =>
        {
            await ((NexusDuplexPipe)pipe).CompleteAsync();
            await list.DisconnectedTask.Timeout(5);
        };

        AppDomain.CurrentDomain.FirstChanceException += onException;
        bool enabled;
        try
        {
            enabled = await list.EnableAsync().Timeout(5);
        }
        finally
        {
            AppDomain.CurrentDomain.FirstChanceException -= onException;
            hooked.EnableTestHook = null;
        }

        await Assert.That(enabled).IsFalse();
        await Assert.That(nres).IsZero();

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
        var nres = 0;
        EventHandler<System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs> onException = (_, e) =>
        {
            if (e.Exception is NullReferenceException)
                Interlocked.Increment(ref nres);
        };

        hooked.EnableTestHook = async _ =>
        {
            await server.StopAsync().Timeout(5);
            await list.DisconnectedTask.Timeout(5);
        };

        AppDomain.CurrentDomain.FirstChanceException += onException;
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
            AppDomain.CurrentDomain.FirstChanceException -= onException;
            hooked.EnableTestHook = null;
        }

        using (Assert.Multiple())
        {
            await Assert.That(error).IsNull();
            await Assert.That(enabled).IsFalse();
            await Assert.That(nres).IsZero();
        }
    }

    private static async Task WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(10);
    }
}
