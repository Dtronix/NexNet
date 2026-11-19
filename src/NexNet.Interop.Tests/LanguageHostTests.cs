using System.Text.Json.Nodes;
using NexNet.Interop.Hosts;
using NexNet.Interop.Remote;
using NexNet.Interop.Tests.Fake;
using NexNet.InteropServer;
using NexNet.TestNexus.Interop;

namespace NexNet.Interop.Tests;

public class LanguageHostTests
{
    private static readonly LanguageHostOptions FastPings = new()
    {
        PingInterval = TimeSpan.FromMilliseconds(100),
        PingTimeout = TimeSpan.FromMilliseconds(600),
        RegisterLanguage = false,
    };

    private static async Task<FakeHost> CurrentFake(FakeHostLauncher launcher)
        => await launcher.Launched.Last().Host.WaitAsync(TimeSpan.FromSeconds(10));

    [Test]
    public async Task Start_LaunchesWithControlAndToken_AndRunsTheWarmUp()
    {
        var launcher = new FakeHostLauncher();
        await using var host = new LanguageHost("fake", FakeHostLauncher.Config(), launcher, FastPings);
        await host.StartAsync().WaitAsync(TimeSpan.FromSeconds(20));

        var arguments = launcher.Arguments.Single();
        await Assert.That(arguments[0]).IsEqualTo("--control");
        await Assert.That(arguments[1]).StartsWith("127.0.0.1:");
        await Assert.That(arguments[2]).IsEqualTo("--token");
        await Assert.That(arguments[3].Length).IsEqualTo(64);

        await Assert.That(host.Generation).IsEqualTo(1);
        await Assert.That(host.Hello!.Lang).IsEqualTo("fake");
        var ops = (await CurrentFake(launcher)).Ops.ToArray();
        await Assert.That(ops.Take(3)).IsEquivalentTo(new[] { "ping", "test.begin", "test.end" }, TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task UnansweredPings_AreACrash_InFlightRequestsFail_AndTheHostRestarts()
    {
        var launcher = new FakeHostLauncher();
        await using var host = new LanguageHost("fake", FakeHostLauncher.Config(), launcher, FastPings);
        var crashed = new TaskCompletionSource<HostCrash>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Crashed += c => crashed.TrySetResult(c);
        await host.StartAsync().WaitAsync(TimeSpan.FromSeconds(20));
        var first = await host.GetConnectionAsync();

        var inFlight = first.RequestAsync(null, "fake.hang");
        (await CurrentFake(launcher)).AnswerPings = false;

        var error = await Assert.ThrowsExactlyAsync<HostCrashedException>(async () => await inFlight.WaitAsync(TimeSpan.FromSeconds(15)));
        await Assert.That(error!.Message).Contains("no response to ping");
        await Assert.That(error.ThreadDump).Contains("fake thread dump");
        await Assert.That(error.StderrTail).Contains("fake stderr line");

        var crash = await crashed.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await Assert.That(crash.Generation).IsEqualTo(1);
        await Assert.That(launcher.Launched.First().Killed).IsTrue();

        var second = await host.GetConnectionAsync().WaitAsync(TimeSpan.FromSeconds(20));
        await Assert.That(second).IsNotSameReferenceAs(first);
        await Assert.That(host.Generation).IsEqualTo(2);
        await second.RequestAsync(null, "ping", deadline: TimeSpan.FromSeconds(5));
        await Assert.ThrowsExactlyAsync<HostCrashedException>(async () => await first.RequestAsync(null, "ping"));
    }

    [Test]
    public async Task ProcessExit_IsACrash_AndOldHandlesFailWithHostCrashed()
    {
        await using var server = await InteropServerHost.StartAsync<InteropServerNexus, InteropServerNexus.ClientProxy>(() => new InteropServerNexus());
        var launcher = new FakeHostLauncher();
        await using var host = new LanguageHost("fake", FakeHostLauncher.Config(), launcher, FastPings);
        await host.StartAsync().WaitAsync(TimeSpan.FromSeconds(20));

        var target = new ClientTarget("fake", ClientTransport.Tcp);
        await using var client = await host.DriverFactory.CreateAsync<InteropClientNexus, InteropClientNexus.ServerProxy>(
            target, ClientSpec.ForEndpoint(server.Endpoints, ClientTransport.Tcp));
        await client.ConnectAsync();

        (await CurrentFake(launcher)).Crash(exitCode: 3);
        await Assert.ThrowsExactlyAsync<HostCrashedException>(async () => await client.GetStateAsync());

        var restarted = await host.GetConnectionAsync().WaitAsync(TimeSpan.FromSeconds(20));
        await Assert.That(host.Generation).IsEqualTo(2);
        await Assert.That(host.Crashes.Single().Reason).Contains("exited");
        await restarted.RequestAsync(null, "ping", deadline: TimeSpan.FromSeconds(5));

        // A new client on the restarted host works.
        await using var again = await host.DriverFactory.CreateAsync<InteropClientNexus, InteropClientNexus.ServerProxy>(
            target, ClientSpec.ForEndpoint(server.Endpoints, ClientTransport.Tcp));
        await again.ConnectAsync();
        await Assert.That(await again.Proxy<IInteropServerNexus>().EchoString("after")).IsEqualTo("after");
        await again.DisconnectAsync();
    }

    [Test]
    public async Task SchemaMismatch_FailsTheStartWithTheRebuildCommand()
    {
        var launcher = new FakeHostLauncher(_ => new FakeHostOptions { SchemaHash = "stale" });
        await using var host = new LanguageHost(
            "fake",
            FakeHostLauncher.Config(),
            launcher,
            FastPings with { ExpectedSchemaHash = "current", RebuildCommand = "rebuild-fake" });

        var error = await Assert.ThrowsExactlyAsync<ControlProtocolException>(async () => await host.StartAsync().WaitAsync(TimeSpan.FromSeconds(20)));
        await Assert.That(error!.Message).Contains("Rebuild it: rebuild-fake");
        await Assert.That(error.Message).Contains("stale");
    }

    [Test]
    public async Task Pool_RegistersDrivers_AndFailsRowsOfHostsThatCannotStart()
    {
        var launcher = new FakeHostLauncher();
        await using var pool = new LanguageHostPool(
            ["csharp", "fake-ok", "fake-missing"],
            language => language == "fake-missing" ? throw new HostNotBuiltException("not built: run build-it") : FakeHostLauncher.Config(),
            launcher,
            () => FastPings);
        await pool.InitializeAsync();

        await Assert.That(pool.Languages).IsEquivalentTo(new[] { "fake-ok", "fake-missing" });
        await Assert.That(pool.Hosts.Keys).IsEquivalentTo(new[] { "fake-ok" });
        await Assert.That(pool.Failures["fake-missing"]).IsTypeOf<HostNotBuiltException>();

        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            async () => await Harness.CreateClientAsync(new ClientTarget("fake-missing", ClientTransport.Tcp), new ClientSpec { Address = "tcp://127.0.0.1:1" }));
        await Assert.That(error!.Message).Contains("run build-it");
    }

    [Test]
    public async Task Dispose_ShutsTheHostDown()
    {
        var launcher = new FakeHostLauncher();
        var host = new LanguageHost("fake", FakeHostLauncher.Config(), launcher, FastPings);
        await host.StartAsync().WaitAsync(TimeSpan.FromSeconds(20));
        var fake = await CurrentFake(launcher);
        await host.DisposeAsync();
        await fake.Exited.WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.That(fake.Ops).Contains("host.shutdown");
        await Assert.That(host.Crashes.Count).IsEqualTo(0);
    }
}
