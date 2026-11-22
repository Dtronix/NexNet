using NexNet.Interop.Hosts;
using NexNet.Interop.Tests.Fake;
using TUnit.Core.Interfaces;

namespace NexNet.Interop.Tests;

/// <summary>
/// A <see cref="LanguageHostPool"/> of one fake language whose hello reports only TCP, initialized as TUnit initializes
/// the pool: during discovery.
/// </summary>
public sealed class DiscoveryHostPool : IAsyncDiscoveryInitializer, IAsyncDisposable
{
    /// <summary>The fake language; its built-in entry (unknown language) has every transport and no capability.</summary>
    public const string Language = "fake-discovery";

    private readonly LanguageHostPool _pool = new(
        [Language],
        _ => FakeHostLauncher.Config(),
        new FakeHostLauncher(_ => new FakeHostOptions { Language = Language, Capabilities = ["Tcp", "FakeTime"], Transports = ["Tcp"] }),
        () => new LanguageHostOptions { RegisterLanguage = true });

    public LanguageHostPool Pool => _pool;

    public Task InitializeAsync() => _pool.InitializeAsync();

    public ValueTask DisposeAsync() => _pool.DisposeAsync();
}

[ClassDataSource<DiscoveryHostPool>(Shared = SharedType.PerTestSession)]
public class HostPoolDiscoveryTests(DiscoveryHostPool hosts)
{
    [Test]
    public async Task LanguageHostPool_IsInitializedAtDiscovery()
    {
        await Assert.That(typeof(IAsyncDiscoveryInitializer).IsAssignableFrom(typeof(LanguageHostPool))).IsTrue();
    }

    [Test]
    [ClientMatrix(Languages = [DiscoveryHostPool.Language], Transports = [ClientTransport.Tcp, ClientTransport.WebSocket], Requires = ClientCapabilities.FakeTime)]
    public async Task Rows_AreBuiltFromTheCapabilitiesOfTheHello(ClientTarget target)
    {
        await Assert.That(hosts.Pool.Hosts.Keys).Contains(DiscoveryHostPool.Language);

        // Without the hello the rows would use the unknown-language entry: every transport, no capability, so FakeTime
        // would rule out both rows. The hello reports TCP and FakeTime: the TCP row only.
        await Assert.That(target.Transport).IsEqualTo(ClientTransport.Tcp);
        await Assert.That(target.SkipReason).IsNull();
        var plan = new ClientMatrixAttribute { Languages = [DiscoveryHostPool.Language], Transports = [ClientTransport.Tcp, ClientTransport.WebSocket], Requires = ClientCapabilities.FakeTime }
            .Plan(transportOverride: null);
        await Assert.That(plan.Rows).IsEquivalentTo(new[] { target });
        await Assert.That(plan.NotApplicable).IsEquivalentTo(new[]
        {
            new NotApplicableRow(DiscoveryHostPool.Language, ClientTransport.WebSocket, $"The {DiscoveryHostPool.Language} client has no WebSocket transport."),
        });
    }

    [Test]
    public async Task InitializeAsync_RunsOnce()
    {
        var launcher = new FakeHostLauncher();
        await using var pool = new LanguageHostPool(["fake-once"], _ => FakeHostLauncher.Config(), launcher, () => new LanguageHostOptions { RegisterLanguage = false });
        var first = pool.InitializeAsync();
        var second = pool.InitializeAsync();
        await Assert.That(second).IsSameReferenceAs(first);
        await first.WaitAsync(TimeSpan.FromSeconds(20));
        await pool.InitializeAsync();
        await Assert.That(launcher.Arguments.Count).IsEqualTo(1);
    }
}
