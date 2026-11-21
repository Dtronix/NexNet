using System.Text.Json.Nodes;
using NexNet.Collections;
using NexNet.Invocation;

namespace NexNet.Interop.Remote;

/// <summary>
/// Creates clients in a language host (<see cref="RemoteClientDriver"/>): opens the running test's scope on the host
/// (<see cref="RemoteTestAudit"/>) and sends <c>client.create</c>; and client pools (<see cref="RemotePoolDriver"/>,
/// <c>pool.create</c>).
/// </summary>
public sealed class RemoteClientDriverFactory : IClientDriverFactory, IPoolDriverFactory
{
    private readonly Func<CancellationToken, Task<ControlConnection>> _connection;

    /// <summary>Creates a factory.</summary>
    /// <param name="language">The language of the host.</param>
    /// <param name="connection">Returns the host's current control connection (waits for a restart in progress).</param>
    public RemoteClientDriverFactory(string language, Func<CancellationToken, Task<ControlConnection>> connection)
    {
        Language = language ?? throw new ArgumentNullException(nameof(language));
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
    }

    /// <summary>The language of the host.</summary>
    public string Language { get; }

    /// <inheritdoc />
    public async Task<IClientUnderTest> CreateAsync<TClientNexus, TServerProxy>(ClientTarget target, ClientSpec spec)
        where TClientNexus : ClientNexusBase<TServerProxy>, IInvocationMethodHash, ICollectionConfigurer, new()
        where TServerProxy : ProxyInvocationBase, IInvocationMethodHash, new()
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(spec);
        var connection = await _connection(CancellationToken.None).ConfigureAwait(false);
        var test = await RemoteTestAudit.BeginAsync(connection, Language).ConfigureAwait(false);
        var (client, server) = ControlJson.NexusPair<TClientNexus, TServerProxy>();
        var args = new JsonObject
        {
            ["spec"] = ControlJson.ToJson(spec),
            ["nexus"] = new JsonObject { ["client"] = client, ["server"] = server },
        };

        var result = await connection.RequestAsync(test, "client.create", args, RemoteClientDriver.DefaultOpMs).ConfigureAwait(false);
        var handle = result["h"]?.GetValue<string>() ?? throw new ControlProtocolException("client.create returned no handle.");
        return new RemoteClientDriver(connection, test, handle, target, client, server);
    }

    /// <inheritdoc />
    public async Task<IPoolUnderTest> CreatePoolAsync<TClientNexus, TServerProxy>(ClientTarget target, ClientSpec spec, ClientPoolSpec pool)
        where TClientNexus : ClientNexusBase<TServerProxy>, IInvocationMethodHash, ICollectionConfigurer, new()
        where TServerProxy : ProxyInvocationBase, IInvocationMethodHash, new()
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(pool);
        pool.Validate();
        var connection = await _connection(CancellationToken.None).ConfigureAwait(false);
        var test = await RemoteTestAudit.BeginAsync(connection, Language).ConfigureAwait(false);
        var (client, server) = ControlJson.NexusPair<TClientNexus, TServerProxy>();
        var args = new JsonObject
        {
            ["spec"] = ControlJson.ToJson(spec),
            ["nexus"] = new JsonObject { ["client"] = client, ["server"] = server },
            ["options"] = ControlJson.ToJson(pool),
        };

        var result = await connection.RequestAsync(test, "pool.create", args, RemoteClientDriver.DefaultOpMs).ConfigureAwait(false);
        var handle = result["pool"]?.GetValue<string>() ?? throw new ControlProtocolException("pool.create returned no pool handle.");
        return new RemotePoolDriver(connection, test, handle, target, client, server);
    }
}
