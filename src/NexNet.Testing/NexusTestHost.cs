using System;
using System.Collections.Generic;
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
    /// Each call to <paramref name="serverNexusFactory"/> and <paramref name="clientNexusFactory"/>
    /// MUST return a fresh instance: NexNet stores per-session state on the nexus, so sharing
    /// instances across connections corrupts state and hangs the second connect. The harness
    /// detects duplicate instances and throws a clear error rather than silently hanging. Omit
    /// the factory arguments to use the default <c>new TServerNexus()</c> / <c>new TClientNexus()</c>.
    /// </summary>
    public static async Task<NexusTestHost<TServerNexus, TClientProxy, TClientNexus, TServerProxy>> CreateAsync<TServerNexus, TClientProxy, TClientNexus, TServerProxy>(
        Func<TServerNexus>? serverNexusFactory = null,
        Func<TClientNexus>? clientNexusFactory = null)
        where TServerNexus : ServerNexusBase<TClientProxy>, IInvocationMethodHash, ICollectionConfigurer, new()
        where TClientProxy : ProxyInvocationBase, IInvocationMethodHash, new()
        where TClientNexus : ClientNexusBase<TServerProxy>, IMethodInvoker, IInvocationMethodHash, ICollectionConfigurer, new()
        where TServerProxy : ProxyInvocationBase, IProxyInvoker, IInvocationMethodHash, new()
    {
        var host = new NexusTestHost<TServerNexus, TClientProxy, TClientNexus, TServerProxy>(
            serverNexusFactory ?? (static () => new TServerNexus()),
            clientNexusFactory ?? (static () => new TClientNexus()));
        await host.StartAsync().ConfigureAwait(false);
        return host;
    }
}

/// <summary>
/// Configured test host: owns the in-process server, recorder/tracker state, and connect
/// helpers. One host per logical scenario; dispose to stop the server and release the
/// rendezvous endpoint.
/// </summary>
public sealed partial class NexusTestHost<TServerNexus, TClientProxy, TClientNexus, TServerProxy>
    : IAsyncDisposable
    where TServerNexus : ServerNexusBase<TClientProxy>, IInvocationMethodHash, ICollectionConfigurer, new()
    where TClientProxy : ProxyInvocationBase, IInvocationMethodHash, new()
    where TClientNexus : ClientNexusBase<TServerProxy>, IMethodInvoker, IInvocationMethodHash, ICollectionConfigurer, new()
    where TServerProxy : ProxyInvocationBase, IProxyInvoker, IInvocationMethodHash, new()
{
    private readonly Func<TServerNexus> _serverNexusFactory;
    private readonly Func<TClientNexus> _clientNexusFactory;
    private readonly HashSet<object> _seenServerNexuses = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<object> _seenClientNexuses = new(ReferenceEqualityComparer.Instance);
    private readonly string _endpoint;
    private readonly InProcessServerConfig _serverConfig;
    private readonly NexusServer<TServerNexus, TClientProxy> _server;
    private readonly QuiescenceTracker _tracker = new();
    private readonly TestAuthenticationStore _authStore = new();
    private readonly TestInvocationInterceptor _interceptor;
    private readonly TestPipeFactory _pipeFactory;
    internal QuiescenceTracker Tracker => _tracker;
    internal TestAuthenticationStore AuthStore => _authStore;

    internal NexusTestHost(Func<TServerNexus> serverNexusFactory, Func<TClientNexus> clientNexusFactory)
    {
        _serverNexusFactory = serverNexusFactory;
        _clientNexusFactory = clientNexusFactory;
        _endpoint = $"nexus-test-{Guid.NewGuid():N}";

        var counters = _tracker.GetCountersFor(0);
        var recorder = new InvocationRecorder();
        _interceptor = new TestInvocationInterceptor(recorder, counters, _tracker);
        _pipeFactory = new TestPipeFactory(counters, _tracker);

        _serverConfig = new InProcessServerConfig
        {
            Endpoint = _endpoint,
            Authenticate = true,
            InvocationInterceptor = _interceptor,
            PipeFactory = _pipeFactory,
            OnAuthenticateOverride = _authStore.OverrideDelegate,
            Counters = counters,
            Tracker = _tracker,
            InternalOnSessionSetup = RegisterSessionWithTracker,
        };

        _server = new NexusServer<TServerNexus, TClientProxy>(_serverConfig, WrappedServerFactory, null);
        ServerRecorder = recorder;
    }

    /// <summary>
    /// Wraps the user-supplied server-nexus factory with duplicate-instance detection. Returning
    /// the same instance on a subsequent connect would silently corrupt per-session state on the
    /// shared <c>SessionContext</c>; surfacing it as an exception turns a multi-client hang into
    /// a discoverable misuse error.
    /// </summary>
    private TServerNexus WrappedServerFactory()
    {
        var instance = _serverNexusFactory();
        lock (_seenServerNexuses)
        {
            if (!_seenServerNexuses.Add(instance))
                throw new InvalidOperationException(
                    "serverNexusFactory returned an instance that was already used by a previous session. " +
                    "Each session needs its own nexus instance because NexNet stores per-session state on it " +
                    "(SessionContext, identity, etc.). Use `() => new TServerNexus()` and capture cross-session " +
                    "state in static fields, closures, or external collaborators instead.");
        }
        return instance;
    }

    /// <summary>
    /// Registers a freshly-constructed session's <c>PendingInvocationCount</c> probe with the
    /// tracker so quiescence accounts for invocations the registry knows about. The session
    /// instance is used as the probe key, so the matching unregister can be a single lookup.
    /// </summary>
    private void RegisterSessionWithTracker(NexNet.Internals.INexusSession session)
    {
        var stateManager = session.SessionInvocationStateManager;
        _tracker.RegisterPendingInvocationProbe(session, () => stateManager.PendingInvocationCount);
    }

    /// <summary>Recorder capturing every invocation the server dispatched. Internal until
    /// Phase 12 exposes it via assertion helpers.</summary>
    internal InvocationRecorder ServerRecorder { get; }

    /// <summary>
    /// Lazily-initialised view onto the server's group registry. <c>host.Groups["editors"].Members</c>
    /// yields the session ids currently in <c>"editors"</c>; <c>host.Groups["editors"].Count</c>
    /// returns the size. Reads snapshot the live registry, so callers can write tests like
    /// <c>Assert.That(host.Groups["editors"].Members, Has.Length.EqualTo(3))</c>.
    /// </summary>
    public GroupIntrospector Groups
    {
        get
        {
            var sm = _server.SessionManagerInternal
                ?? throw new System.InvalidOperationException(
                    "Server has not been started yet; call CreateAsync to obtain a started host.");
            return new GroupIntrospector(sm.Groups);
        }
    }

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
        var clientConfig = new InProcessClientConfig
        {
            Endpoint = _endpoint,
            InternalOnSessionSetup = RegisterSessionWithTracker,
            InvocationInterceptor = _interceptor,
            PipeFactory = _pipeFactory,
        };
        if (identity is not null)
        {
            var token = _authStore.IssueToken(identity);
            clientConfig.Authenticate = () => token;
        }

        var clientNexus = _clientNexusFactory();
        lock (_seenClientNexuses)
        {
            if (!_seenClientNexuses.Add(clientNexus))
                throw new InvalidOperationException(
                    "clientNexusFactory returned an instance that was already used by a previous client. " +
                    "Each client needs its own nexus instance because NexNet stores per-session state on it. " +
                    "Use `() => new TClientNexus()` and capture cross-client state externally instead.");
        }
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
