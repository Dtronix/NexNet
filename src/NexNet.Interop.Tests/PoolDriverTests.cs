using NexNet.Interop.Hosts;
using NexNet.Interop.Tests.Fake;
using NexNet.InteropServer;
using NexNet.Messages;
using NexNet.TestNexus.Interop;

namespace NexNet.Interop.Tests;

/// <summary>
/// The client pool surface (C-6) on both drivers: the in-process C# pool, and the remote pool driver over the control
/// protocol (§6.11) against the fake host. Every scenario runs the same assertions on both, so the protocol carries
/// every pool behaviour the in-process row has.
/// </summary>
public class PoolDriverTests
{
    private const double Wait = 10;
    private const string InProc = "inproc";
    private const string Remote = "remote";

    private static readonly LanguageHostOptions Options = new() { RegisterLanguage = false };

    private static Task<InteropServer<InteropServerNexus, InteropServerNexus.ClientProxy>> StartServer(InteropServerOptions? options = null)
        => InteropServerHost.StartAsync<InteropServerNexus, InteropServerNexus.ClientProxy>(() => new InteropServerNexus(), options);

    /// <summary>Creates pools through one driver: the harness (in process) or a fake language host.</summary>
    private sealed class Rig : IAsyncDisposable
    {
        private LanguageHost? _host;

        public static async Task<Rig> StartAsync(string driver)
        {
            var rig = new Rig();
            if (driver == Remote)
            {
                rig._host = new LanguageHost("fake", FakeHostLauncher.Config(), new FakeHostLauncher(), Options);
                await rig._host.StartAsync().WaitAsync(TimeSpan.FromSeconds(20));
            }

            return rig;
        }

        public Task<IPoolUnderTest> CreatePoolAsync(InteropEndpoints endpoints, ClientPoolSpec pool, Action<ClientSpec>? configure = null)
        {
            var spec = ClientSpec.ForEndpoint(endpoints, ClientTransport.Tcp);
            configure?.Invoke(spec);
            return _host == null
                ? Harness.CreatePoolAsync(ClientTarget.CSharp(ClientTransport.Tcp), spec, pool)
                : _host.DriverFactory.CreatePoolAsync<InteropClientNexus, InteropClientNexus.ServerProxy>(
                    new ClientTarget("fake", ClientTransport.Tcp), spec, pool);
        }

        public async ValueTask DisposeAsync()
        {
            if (_host != null)
                await _host.DisposeAsync();
        }
    }

    private static async Task AssertState(IPoolUnderTest pool, int max, int idle, int rented)
        => await Assert.That(await pool.GetStateAsync()).IsEqualTo(new ClientPoolState(max, idle, rented));

    private static async Task AssertNoLeaks(IPoolUnderTest pool)
    {
        var report = await pool.EndTestAuditAsync();
        await Assert.That(report.IsClean).IsTrue().Because(report.ToString());
    }

    [Test]
    [Arguments(InProc)]
    [Arguments(Remote)]
    public async Task Rent_ReusesReturnedClient_AndReturnedRentalBehavesAsDisposed(string driver)
    {
        await using var server = await StartServer();
        await using var rig = await Rig.StartAsync(driver);
        var pool = await rig.CreatePoolAsync(server.Endpoints, new ClientPoolSpec { MaxConnections = 2 });

        var first = await pool.RentAsync().WaitAsync(TimeSpan.FromSeconds(Wait));
        await Assert.That(await first.GetStateAsync()).IsEqualTo(ConnectionState.Connected);
        var proxy = first.Proxy<IInteropServerNexus>();
        await Assert.That(await proxy.EchoString("hi")).IsEqualTo("hi");
        await Assert.That(await first.EnsureConnectedAsync()).IsTrue();
        await AssertState(pool, 2, 0, 1);

        await first.DisposeAsync();
        await Assert.That(first.IsReturned).IsTrue();
        await AssertState(pool, 2, 1, 0);

        // A returned rental: as the C# IRentedNexusClient after Dispose.
        await Assert.That(() => first.Proxy<IInteropServerNexus>()).ThrowsExactly<ObjectDisposedException>();
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(async () => await proxy.EchoString("late"));
        await Assert.That(await first.GetStateAsync()).IsEqualTo(ConnectionState.Disconnected);
        await Assert.That(await first.WhenDisconnectedAsync(Wait)).IsEqualTo(DisconnectReason.None);
        await Assert.That(await first.EnsureConnectedAsync()).IsFalse();
        await first.ReturnAsync();
        await AssertState(pool, 2, 1, 0);

        // What the pool owns stays with the pool.
        var second = await pool.RentAsync().WaitAsync(TimeSpan.FromSeconds(Wait));
        await Assert.That(second.ClientId).IsEqualTo(first.ClientId);
        await Assert.That(() => second.ConnectAsync()).ThrowsExactly<NotSupportedException>();
        await Assert.That(() => second.DisconnectAsync()).ThrowsExactly<NotSupportedException>();
        await Assert.That(await second.Proxy<IInteropServerNexus>().EchoString("again")).IsEqualTo("again");
        await second.ReturnAsync();

        await pool.DisposeAsync();
        await pool.DisposeAsync();
        await AssertNoLeaks(pool);
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(async () => await pool.RentAsync());
    }

    [Test]
    [Arguments(InProc)]
    [Arguments(Remote)]
    public async Task Rent_WaitsAtMaxConnections_AndIsCancelable(string driver)
    {
        await using var server = await StartServer();
        await using var rig = await Rig.StartAsync(driver);
        await using var pool = await rig.CreatePoolAsync(server.Endpoints, new ClientPoolSpec { MaxConnections = 1 });

        var held = await pool.RentAsync().WaitAsync(TimeSpan.FromSeconds(Wait));
        using (var cts = new CancellationTokenSource())
        {
            var canceled = pool.RentAsync(cts.Token);
            await Task.Delay(100);
            await Assert.That(canceled.IsCompleted).IsFalse();
            await cts.CancelAsync();
            await Assert.ThrowsAsync<OperationCanceledException>(async () => await canceled.WaitAsync(TimeSpan.FromSeconds(Wait)));
        }

        var waiting = pool.RentAsync();
        await Task.Delay(100);
        await Assert.That(waiting.IsCompleted).IsFalse();
        await held.ReturnAsync();
        await using var next = await waiting.WaitAsync(TimeSpan.FromSeconds(Wait));
        await Assert.That(next.ClientId).IsEqualTo(held.ClientId);
        await AssertState(pool, 1, 0, 1);
    }

    [Test]
    [Arguments(InProc)]
    [Arguments(Remote)]
    public async Task Rent_ConcurrentRentals_NeverExceedMaxConnections(string driver)
    {
        await using var server = await StartServer();
        await using var rig = await Rig.StartAsync(driver);
        await using var pool = await rig.CreatePoolAsync(server.Endpoints, new ClientPoolSpec { MaxConnections = 3 });

        var ids = await Task.WhenAll(Enumerable.Range(0, 12).Select(async i =>
        {
            await using var rental = await pool.RentAsync();
            await Assert.That(await rental.Proxy<IInteropServerNexus>().EchoString("n" + i)).IsEqualTo("n" + i);
            return rental.ClientId;
        })).WaitAsync(TimeSpan.FromSeconds(Wait * 3));

        await Assert.That(ids.Distinct().Count()).IsLessThanOrEqualTo(3);
        var state = await pool.GetStateAsync();
        await Assert.That(state.Rented).IsEqualTo(0);
        await Assert.That(state.Idle).IsLessThanOrEqualTo(3);
    }

    [Test]
    [Arguments(InProc)]
    [Arguments(Remote)]
    public async Task Rent_ConnectionFailure_ThrowsClientPoolConnectionException_AndReleasesTheSlot(string driver)
    {
        await using var server = await StartServer();
        await server.StopAsync();
        await using var rig = await Rig.StartAsync(driver);
        await using var pool = await rig.CreatePoolAsync(server.Endpoints, new ClientPoolSpec { MaxConnections = 1 });

        for (var i = 0; i < 2; i++)
        {
            var error = await Assert.ThrowsExactlyAsync<ClientPoolConnectionException>(
                async () => await pool.RentAsync().WaitAsync(TimeSpan.FromSeconds(Wait)));
            await Assert.That(error!.ConnectionResult.Success).IsFalse();
        }

        await AssertState(pool, 1, 0, 0);
    }

    [Test]
    [Arguments(InProc)]
    [Arguments(Remote)]
    public async Task Rent_AuthenticationRefused_ReportsTheDisconnectReason(string driver)
    {
        await using var server = await StartServer(new InteropServerOptions
        {
            TokenValidator = (_, _) => ValueTask.FromResult<IIdentity?>(null),
        });
        await using var rig = await Rig.StartAsync(driver);
        await using var pool = await rig.CreatePoolAsync(server.Endpoints, new ClientPoolSpec { MaxConnections = 1 }, spec => spec.AuthToken = "bad");

        var error = await Assert.ThrowsExactlyAsync<ClientPoolConnectionException>(
            async () => await pool.RentAsync().WaitAsync(TimeSpan.FromSeconds(Wait)));
        await Assert.That(error!.ConnectionResult.DisconnectReason).IsEqualTo(DisconnectReason.Authentication);
    }

    [Test]
    [Arguments(InProc)]
    [Arguments(Remote)]
    public async Task EnsureConnected_ReconnectsAfterServerRestart_AndUnhealthyReturnsAreDropped(string driver)
    {
        await using var server = await StartServer();
        await using var rig = await Rig.StartAsync(driver);
        await using var pool = await rig.CreatePoolAsync(server.Endpoints, new ClientPoolSpec { MaxConnections = 1 });

        var rental = await pool.RentAsync().WaitAsync(TimeSpan.FromSeconds(Wait));
        var firstId = rental.ClientId;
        await server.StopAsync();
        await rental.WaitForStateAsync(ConnectionState.Disconnected, Wait);
        await Assert.That(await rental.EnsureConnectedAsync()).IsFalse();

        await server.StartAsync();
        await Assert.That(await rental.EnsureConnectedAsync()).IsTrue();
        await rental.WaitForStateAsync(ConnectionState.Connected, Wait);
        await Assert.That(await rental.Proxy<IInteropServerNexus>().EchoString("back")).IsEqualTo("back");

        // A client returned disconnected is disposed, not kept idle; the next rent creates a new client.
        await server.StopAsync();
        await rental.WaitForStateAsync(ConnectionState.Disconnected, Wait);
        await rental.ReturnAsync();
        await AssertState(pool, 1, 0, 0);

        await server.StartAsync();
        await using var fresh = await pool.RentAsync().WaitAsync(TimeSpan.FromSeconds(Wait));
        await Assert.That(fresh.ClientId).IsNotEqualTo(firstId);
        await Assert.That(await fresh.GetStateAsync()).IsEqualTo(ConnectionState.Connected);
    }

    [Test]
    [Arguments(InProc)]
    [Arguments(Remote)]
    public async Task FakeTime_HealthCheckEvictsIdleClientsDownToMinIdle(string driver)
    {
        await using var server = await StartServer();
        await using var rig = await Rig.StartAsync(driver);
        await using var pool = await rig.CreatePoolAsync(
            server.Endpoints,
            new ClientPoolSpec { MaxConnections = 5, MaxIdleTimeMs = 100, MinIdleConnections = 1 },
            spec => spec.FakeTime = true);

        var rentals = new List<IRentedClientUnderTest>();
        for (var i = 0; i < 3; i++)
            rentals.Add(await pool.RentAsync().WaitAsync(TimeSpan.FromSeconds(Wait)));
        foreach (var rental in rentals)
            await rental.ReturnAsync();
        await AssertState(pool, 5, 3, 0);

        // Health check every max(100 ms, MaxIdleTime / 4); timers due fire before AdvanceTimeAsync completes.
        await pool.AdvanceTimeAsync(TimeSpan.FromMilliseconds(500));
        await AssertState(pool, 5, 1, 0);

        // A rental moves the clock of its pool.
        await using var last = await pool.RentAsync().WaitAsync(TimeSpan.FromSeconds(Wait));
        await last.AdvanceTimeAsync(TimeSpan.FromMilliseconds(500));
        await AssertState(pool, 5, 0, 1);
    }

    [Test]
    [Arguments(InProc)]
    [Arguments(Remote)]
    public async Task AdvanceTime_WithoutFakeTime_IsInvalid(string driver)
    {
        await using var server = await StartServer();
        await using var rig = await Rig.StartAsync(driver);
        await using var pool = await rig.CreatePoolAsync(server.Endpoints, new ClientPoolSpec());
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () => await pool.AdvanceTimeAsync(TimeSpan.FromSeconds(1)));
        await AssertState(pool, ClientPoolSpec.DefaultMaxConnections, 0, 0);
    }

    [Test]
    [Arguments(InProc)]
    [Arguments(Remote)]
    public async Task Audit_CountsPoolClientsUntilReturnedAndDisposed(string driver)
    {
        await using var server = await StartServer();
        await using var rig = await Rig.StartAsync(driver);
        var pool = await rig.CreatePoolAsync(server.Endpoints, new ClientPoolSpec { MaxConnections = 2 });

        var idle = await pool.RentAsync().WaitAsync(TimeSpan.FromSeconds(Wait));
        var held = await pool.RentAsync().WaitAsync(TimeSpan.FromSeconds(Wait));
        await idle.ReturnAsync();

        var report = await pool.EndTestAuditAsync();
        await Assert.That(report.OpenClients).IsEqualTo(2).Because(report.ToString());
        await Assert.That(report.Timers).IsEqualTo(1).Because(report.ToString());

        // Disposing the pool disposes its idle clients; a rental not returned stays connected, so it still leaks.
        await pool.DisposeAsync();
        report = await pool.EndTestAuditAsync();
        await Assert.That(report.OpenClients).IsEqualTo(1).Because(report.ToString());
        await Assert.That(report.Timers).IsEqualTo(0).Because(report.ToString());

        try
        {
            await held.ReturnAsync();
        }
        catch (InvalidOperationException)
        {
            // Known C# pool bug (reported with C-6): NexusClientPool.ReturnClient disposes the client of a disposed pool,
            // then releases the pool's disposed semaphore and throws ObjectDisposedException. The client is gone either
            // way, which is what the audit below checks.
        }

        await AssertNoLeaks(pool);
    }

    [Test]
    [Arguments(InProc)]
    [Arguments(Remote)]
    public async Task CreatePool_OutOfRangeOptions_ThrowOnEveryDriver(string driver)
    {
        await using var server = await StartServer();
        await using var rig = await Rig.StartAsync(driver);
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(
            async () => await rig.CreatePoolAsync(server.Endpoints, new ClientPoolSpec { MaxConnections = 0 }));
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(
            async () => await rig.CreatePoolAsync(server.Endpoints, new ClientPoolSpec { MinIdleConnections = -1 }));
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(
            async () => await rig.CreatePoolAsync(server.Endpoints, new ClientPoolSpec { MaxIdleTimeMs = -1 }));

        // MinIdleConnections above MaxConnections is allowed.
        await using var pool = await rig.CreatePoolAsync(server.Endpoints, new ClientPoolSpec { MaxConnections = 2, MinIdleConnections = 5 });
        await AssertState(pool, 2, 0, 0);
    }
}
