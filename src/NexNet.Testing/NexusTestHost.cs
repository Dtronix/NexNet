using System;
using System.Threading.Tasks;
using NexNet.Collections;
using NexNet.Invocation;
using NexNet.Testing.Authentication;
using NexNet.Testing.Quiescence;
using NexNet.Testing.Recording;
using NexNet.Testing.Streaming;
using NexNet.Testing.Transports.InProcess;

namespace NexNet.Testing;

/// <summary>
/// Static entry point for the test harness. Use <see cref="CreateAsync"/> to construct a
/// configured host bound to a fresh in-process endpoint with the recorder, pipe tap, and
/// auth override pre-installed.
/// </summary>
public static class NexusTestHost
{
    /// <summary>
    /// Creates and starts a <see cref="NexusTestHost{TServerNexus, TClientProxy, TClientNexus, TServerProxy}"/>.
    /// The server's nexus is constructed via <paramref name="serverNexusFactory"/> once per
    /// connection; the same client nexus instance supplied via <paramref name="clientNexusFactory"/>
    /// is used for the next call to <c>ConnectAsync</c>.
    /// </summary>
    public static async Task<NexusTestHost<TServerNexus, TClientProxy, TClientNexus, TServerProxy>> CreateAsync<TServerNexus, TClientProxy, TClientNexus, TServerProxy>(
        Func<TServerNexus> serverNexusFactory,
        Func<TClientNexus> clientNexusFactory)
        where TServerNexus : ServerNexusBase<TClientProxy>, IInvocationMethodHash, ICollectionConfigurer, new()
        where TClientProxy : ProxyInvocationBase, IInvocationMethodHash, new()
        where TClientNexus : ClientNexusBase<TServerProxy>, IMethodInvoker, IInvocationMethodHash, ICollectionConfigurer, new()
        where TServerProxy : ProxyInvocationBase, IProxyInvoker, IInvocationMethodHash, new()
    {
        var host = new NexusTestHost<TServerNexus, TClientProxy, TClientNexus, TServerProxy>(
            serverNexusFactory, clientNexusFactory);
        await host.StartAsync().ConfigureAwait(false);
        return host;
    }
}

/// <summary>
/// Configured test host: owns the in-process server, recorder/tracker state, and connect
/// helpers. One host per logical scenario; dispose to stop the server and release the
/// rendezvous endpoint.
/// </summary>
public sealed class NexusTestHost<TServerNexus, TClientProxy, TClientNexus, TServerProxy>
    : IAsyncDisposable
    where TServerNexus : ServerNexusBase<TClientProxy>, IInvocationMethodHash, ICollectionConfigurer, new()
    where TClientProxy : ProxyInvocationBase, IInvocationMethodHash, new()
    where TClientNexus : ClientNexusBase<TServerProxy>, IMethodInvoker, IInvocationMethodHash, ICollectionConfigurer, new()
    where TServerProxy : ProxyInvocationBase, IProxyInvoker, IInvocationMethodHash, new()
{
    private readonly Func<TServerNexus> _serverNexusFactory;
    private readonly Func<TClientNexus> _clientNexusFactory;
    private readonly string _endpoint;
    private readonly InProcessServerConfig _serverConfig;
    private readonly NexusServer<TServerNexus, TClientProxy> _server;
    private readonly QuiescenceTracker _tracker = new();
    private readonly TestAuthenticationStore _authStore = new();
    internal QuiescenceTracker Tracker => _tracker;
    internal TestAuthenticationStore AuthStore => _authStore;

    internal NexusTestHost(Func<TServerNexus> serverNexusFactory, Func<TClientNexus> clientNexusFactory)
    {
        _serverNexusFactory = serverNexusFactory;
        _clientNexusFactory = clientNexusFactory;
        _endpoint = $"nexus-test-{Guid.NewGuid():N}";

        var counters = _tracker.GetCountersFor(0);
        var recorder = new InvocationRecorder();
        var interceptor = new TestInvocationInterceptor(recorder, counters, _tracker);
        var pipeFactory = new TestPipeFactory(counters, _tracker);

        _serverConfig = new InProcessServerConfig
        {
            Endpoint = _endpoint,
            Authenticate = true,
            InvocationInterceptor = interceptor,
            PipeFactory = pipeFactory,
            OnAuthenticateOverride = _authStore.OverrideDelegate,
        };

        _server = new NexusServer<TServerNexus, TClientProxy>(_serverConfig, _serverNexusFactory, null);
        ServerRecorder = recorder;
    }

    /// <summary>Recorder capturing every invocation the server dispatched. Internal until
    /// Phase 12 exposes it via assertion helpers.</summary>
    internal InvocationRecorder ServerRecorder { get; }

    /// <summary>Returns when every counter the tracker watches has been zero across a yield.</summary>
    public Task QuiesceAsync() => _tracker.QuiesceAsync();

    internal async Task StartAsync() => await _server.StartAsync().ConfigureAwait(false);

    /// <summary>
    /// Connects an anonymous client to the host. The default identity is empty; use
    /// <see cref="ConnectAsAsync"/> for fake-identity scenarios.
    /// </summary>
    public Task<NexusTestClient<TClientNexus, TServerProxy>> ConnectAsync()
        => ConnectAsAsync(identity: null);

    /// <summary>
    /// Connects a client and stamps its handshake with the token mapped to <paramref name="identity"/>.
    /// The server's auth-override consults the harness's token store; if <paramref name="identity"/>
    /// is null an empty token is sent and the override returns null (so callers exercising
    /// auth-must-fail paths can do so without disabling the override).
    /// </summary>
    public async Task<NexusTestClient<TClientNexus, TServerProxy>> ConnectAsAsync(IIdentity? identity)
    {
        var clientConfig = new InProcessClientConfig { Endpoint = _endpoint };
        if (identity is not null)
        {
            var token = _authStore.IssueToken(identity);
            clientConfig.Authenticate = () => token;
        }

        var clientNexus = _clientNexusFactory();
        var client = new NexusClient<TClientNexus, TServerProxy>(clientConfig, clientNexus);
        await client.ConnectAsync().ConfigureAwait(false);
        return new NexusTestClient<TClientNexus, TServerProxy>(client, clientNexus);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        try { await _server.StopAsync().ConfigureAwait(false); }
        catch { /* idempotent */ }
    }
}
