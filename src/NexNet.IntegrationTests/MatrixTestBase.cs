using NexNet.IntegrationTests.Pipes;
using NexNet.IntegrationTests.TestInterfaces;
using NexNet.Interop;
using NexNet.Interop.Hosts;
using NexNet.Interop.InProc;
using NexNet.InteropServer;
using NexNet.Invocation;
using NexNet.Logging;
using NexNet.Messages;
using NexNet.Quic;
using NexNet.Transports;
using NexNet.Transports.Uds;
using System.Threading.Channels;
#pragma warning disable VSTHRD200

namespace NexNet.IntegrationTests;

/// <summary>
/// Base of the tests on the client matrix (P01): the client under test is created through <see cref="Harness"/> for each
/// <see cref="ClientTarget"/> row (the C# client in process, or a language host's client), the server is C#, and what
/// the test checks about the client is observed on the server or through <see cref="IClientUnderTest"/>.
/// </summary>
/// <remarks>
/// <para>
/// Most tests use the basic test nexus: a dedicated interop server of <see cref="ServerNexus"/> on every interop endpoint
/// (<see cref="StartServerAsync"/>) and clients of <see cref="ClientNexus"/> (<see cref="ConnectAsync(ClientTarget, InteropServer{ServerNexus, ServerNexus.ClientProxy}, Action{ClientNexusScript}?, Action{ClientSpec}?)"/>).
/// Tests of another server (authorization, versioning, TLS) build the client spec of their own server with
/// <see cref="Spec"/>. The pipe and collection helpers are in the other parts of this class.
/// </para>
/// <para>
/// Derived classes take the <see cref="LanguageHostPool"/> (<c>[ClassDataSource&lt;LanguageHostPool&gt;(Shared =
/// SharedType.PerTestSession)]</c>), so the hosts of NEXNET_TEST_LANGS are probed before their matrix rows are built.
/// The C# suite's QUIC rows are C#-only rows (<see cref="ClientMatrixAttribute.CSharpTransports"/>): no other client
/// language has QUIC.
/// </para>
/// <para>
/// The static helpers are internal so that tests which cannot derive from this class (the ASP.NET tests derive from
/// <see cref="BaseAspTests"/>) can use them too.
/// </para>
/// </remarks>
internal abstract partial class MatrixTestBase(LanguageHostPool hosts) : BaseTests
{
    private readonly TaskCompletionSource<ServerNexus> _connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Channel<int> _reports = Channel.CreateUnbounded<int>();

    /// <summary>The language hosts of the session; initializing them registers their drivers with <see cref="Harness"/>.</summary>
    protected LanguageHostPool Hosts { get; } = hosts;

    /// <summary>
    /// When true, every server method of a <see cref="StartServerAsync"/> server completes with its default result unless
    /// the test sets its handler. When false (the default), a method without a handler fails remotely.
    /// </summary>
    protected virtual bool CompleteServerMethodsByDefault => false;

    /// <summary>
    /// Starts a dedicated interop server of <see cref="ServerNexus"/>. <paramref name="nexusCreated"/> sets up each server
    /// nexus; <paramref name="configure"/> is called with the configuration of every endpoint, so taps and limits apply
    /// whatever transport the client uses.
    /// </summary>
    /// <remarks>
    /// The first session's nexus is kept for <see cref="ServerNexusAsync"/>, and <c>ServerTaskWithParam</c> feeds
    /// <see cref="WaitForReportAsync"/> unless <paramref name="nexusCreated"/> replaces their handlers.
    /// </remarks>
    protected async Task<InteropServer<ServerNexus, ServerNexus.ClientProxy>> StartServerAsync(
        Action<ServerNexus>? nexusCreated = null,
        Action<ServerConfig>? configure = null)
    {
        LoggerMode = BasePipeTests.LogMode.OnTestFail;
        var options = new InteropServerOptions
        {
            Logger = Logger,
            ConfigureEndpoint = configure == null ? null : (_, config) => configure(config),
        };

        return await InteropServerHost.StartAsync<ServerNexus, ServerNexus.ClientProxy>(() =>
        {
            var nexus = new ServerNexus();
            if (CompleteServerMethodsByDefault)
                CompleteByDefault(nexus);
            nexus.OnConnectedEvent = connected =>
            {
                _connected.TrySetResult(connected);
                return ValueTask.CompletedTask;
            };
            nexus.ServerTaskWithParamEvent = (_, value) =>
            {
                _reports.Writer.TryWrite(value);
                return ValueTask.CompletedTask;
            };
            nexusCreated?.Invoke(nexus);
            return nexus;
        }, options).Timeout(T(TestTimeouts.Connect));
    }

    /// <summary>The server nexus of the first session that connected to the <see cref="StartServerAsync"/> server.</summary>
    protected Task<ServerNexus> ServerNexusAsync() => _connected.Task.Timeout(T(1));

    /// <summary>
    /// Creates the client of <paramref name="target"/> for <paramref name="server"/>'s endpoint; it is not connected.
    /// The spec logs to the test's logger before <paramref name="configure"/> runs.
    /// </summary>
    protected Task<IClientUnderTest> CreateClientAsync(
        ClientTarget target,
        InteropServer<ServerNexus, ServerNexus.ClientProxy> server,
        Action<ClientSpec>? configure = null)
    {
        var spec = ClientSpec.ForEndpoint(server.Endpoints, target.Transport);
        spec.Logger = Logger;
        configure?.Invoke(spec);
        return Harness.CreateClientAsync<ClientNexus, ClientNexus.ServerProxy>(target, spec);
    }

    /// <summary>
    /// Creates the client of <paramref name="target"/> for <paramref name="server"/> (<see cref="CreateClientAsync"/>),
    /// lets <paramref name="script"/> set up its client nexus, then connects it; disposes it when that fails.
    /// </summary>
    protected async Task<IClientUnderTest> ConnectAsync(
        ClientTarget target,
        InteropServer<ServerNexus, ServerNexus.ClientProxy> server,
        Action<ClientNexusScript>? script = null,
        Action<ClientSpec>? configure = null)
    {
        var client = await CreateClientAsync(target, server, configure);
        try
        {
            script?.Invoke(Script(client));
            await client.ConnectAsync().Timeout(T(TestTimeouts.Connect));
            return client;
        }
        catch
        {
            await client.DisposeAsync();
            throw;
        }
    }

    /// <summary>The server proxy of <paramref name="client"/>.</summary>
    protected static Remote_IServerNexus Proxy(IClientUnderTest client) => client.Proxy<Remote_IServerNexus>();

    /// <summary>What <paramref name="client"/> does when the server invokes its client nexus.</summary>
    protected static ClientNexusScript Script(IClientUnderTest client) => client.Callbacks<ClientNexusScript>();

    /// <summary>
    /// Waits until the client has handed every started operation to its runtime, then makes a round trip to the
    /// server. Frames on a session arrive in order, so once it returns the server has received everything the client
    /// sent before. Use it before asserting that something did not arrive. A missing server handler fails the call
    /// remotely, which still completes the round trip.
    /// </summary>
    protected static async Task RoundTripAsync(IClientUnderTest client)
    {
        await client.BarrierAsync().Timeout(T(1));
        try
        {
            await Proxy(client).ServerTaskValue().Timeout(T(1));
        }
        catch (ProxyRemoteInvocationException)
        {
            // The server answered.
        }
    }

    /// <summary>
    /// Waits until invocation <paramref name="id"/> is no longer reserved on the client, then asserts it. Every client,
    /// the C# one included, reports how many invocation IDs its session still holds
    /// (<see cref="IClientUnderTest.GetOutstandingInvocationsAsync"/>: calls awaiting their result, and calls canceled
    /// after send until their late reply releases the ID), which must reach 0: the callers check it when no other
    /// invocation is in flight.
    /// </summary>
    /// <remarks>The C# client is also checked by id, since its ID table is visible in process.</remarks>
    protected static async Task AssertNotOutstanding(IClientUnderTest client, ushort id)
    {
        var invocations = (client as InProcClientDriver<ClientNexus, ClientNexus.ServerProxy>)?.Client.Session?.SessionInvocationStateManager;
        if (invocations != null)
        {
            await WaitUntil(() => !invocations.IsOutstanding(id));
            await Assert.That(invocations.IsOutstanding(id)).IsFalse();
        }

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(T(1));
        var outstanding = await client.GetOutstandingInvocationsAsync().Timeout(T(1));
        while (outstanding != 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
            outstanding = await client.GetOutstandingInvocationsAsync().Timeout(T(1));
        }

        await Assert.That(outstanding).IsEqualTo(0);
    }

    /// <summary>
    /// Waits until the client holds <paramref name="expected"/> invocation IDs
    /// (<see cref="IClientUnderTest.GetOutstandingInvocationsAsync"/>), for at most <c>T(1)</c>, then asserts it.
    /// </summary>
    protected static async Task AssertReservedIds(IClientUnderTest client, int expected)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(T(1));
        var reserved = await client.GetOutstandingInvocationsAsync().Timeout(T(1));
        while (reserved != expected && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
            reserved = await client.GetOutstandingInvocationsAsync().Timeout(T(1));
        }

        await Assert.That(reserved).IsEqualTo(expected);
    }

    /// <summary>Waits until the client is in <paramref name="state"/>, for at most <c>T(1)</c>.</summary>
    protected static Task WaitForStateAsync(IClientUnderTest client, ConnectionState state)
        => client.WaitForStateAsync(state, T(1));

    /// <summary>Asserts that <paramref name="client"/> reaches (or stays in) <paramref name="state"/>.</summary>
    protected static async Task AssertState(IClientUnderTest client, ConnectionState state)
    {
        try
        {
            await client.WaitForStateAsync(state, T(1));
        }
        catch (TimeoutException)
        {
            // Reported by the assertion below.
        }

        await Assert.That(await client.GetStateAsync()).IsEqualTo(state);
    }

    /// <summary>Disconnects the client when it is still connected, then asserts its leak audit is clean.</summary>
    internal static async Task EndAsync(IClientUnderTest client)
    {
        if (await client.GetStateAsync() != ConnectionState.Disconnected)
            await client.DisconnectAsync().Timeout(T(TestTimeouts.Connect));

        var report = await client.EndTestAuditAsync().Timeout(T(5));
        await Assert.That(report.IsClean).IsTrue().Because(report.ToString());
    }

    /// <summary>The session id of <paramref name="server"/>'s first connected client.</summary>
    protected static long FirstSessionId(InteropServer<ServerNexus, ServerNexus.ClientProxy> server)
    {
        using var context = server.Server.ContextProvider.Rent();
        return context.Proxy.GetIds().First();
    }

    /// <summary>
    /// Skips a row of another language: the test drives the C# client directly, through <see cref="InProc"/>, because
    /// the harness cannot yet express what it checks (<paramref name="reason"/>).
    /// </summary>
    protected static void RequireInProc(ClientTarget target, string reason)
    {
        if (target.Language != ClientLanguages.CSharp)
            Skip.Test("C# client only: " + reason);
    }

    /// <summary>The C# client of an in-process row (<see cref="RequireInProc"/>).</summary>
    protected static InProcClientDriver<ClientNexus, ClientNexus.ServerProxy> InProc(IClientUnderTest client)
        => (InProcClientDriver<ClientNexus, ClientNexus.ServerProxy>)client;

    /// <summary>Creates a task completion source whose continuations run asynchronously.</summary>
    protected static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <inheritdoc cref="Signal()"/>
    protected static TaskCompletionSource<T> Signal<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    // ----- Servers of other nexuses, created with BaseTests (authorization, versioning, TLS, ASP.NET) -----

    /// <summary>The <see cref="BaseTests.Type"/> of a client-matrix transport.</summary>
    internal static Type ToType(ClientTransport transport) => transport switch
    {
        ClientTransport.Tcp => Type.Tcp,
        ClientTransport.TcpTls => Type.TcpTls,
        ClientTransport.Uds => Type.Uds,
        ClientTransport.HttpSocket => Type.HttpSocket,
        ClientTransport.HttpSocketTls => Type.HttpSocketTls,
        ClientTransport.WebSocket => Type.WebSocket,
        ClientTransport.WebSocketTls => Type.WebSocketTls,
        ClientTransport.Quic => Type.Quic,
        _ => throw new ArgumentOutOfRangeException(nameof(transport), transport, null),
    };

    /// <summary>
    /// The address of the server a C# client configuration (<see cref="BaseTests"/>.CreateClientConfig) connects to,
    /// in the form of <see cref="ClientSpec.Address"/>.
    /// </summary>
    internal static string Address(ClientConfig config) => config switch
    {
        TcpTlsClientConfig tls => "tls://" + tls.EndPoint,
        TcpClientConfig tcp => "tcp://" + tcp.EndPoint,
        UdsClientConfig uds => "uds:" + uds.EndPoint,
        HttpClientConfig http => http.Url.ToString(),
        QuicClientConfig quic => "quic://" + quic.EndPoint,
        _ => throw new ArgumentOutOfRangeException(nameof(config), config.GetType(), null),
    };

    /// <summary>
    /// The spec of a client of <paramref name="transport"/> to the server <paramref name="config"/> connects to; a
    /// secure transport trusts the F08 test CA.
    /// </summary>
    internal static ClientSpec Spec(ClientConfig config, ClientTransport transport, INexusLogger? logger) => new()
    {
        Transport = transport,
        Address = Address(config),
        CaPem = transport.IsSecure() ? File.ReadAllText(InteropCertificates.TestCaPemPath) : null,
        Logger = logger,
    };

    /// <summary>Creates the client of <paramref name="target"/> and connects it; disposes it when the connect fails.</summary>
    internal static async Task<IClientUnderTest> ConnectAsync<TClientNexus, TServerProxy>(ClientTarget target, ClientSpec spec)
        where TClientNexus : ClientNexusBase<TServerProxy>, IInvocationMethodHash, NexNet.Collections.ICollectionConfigurer, new()
        where TServerProxy : ProxyInvocationBase, IInvocationMethodHash, new()
    {
        var client = await Harness.CreateClientAsync<TClientNexus, TServerProxy>(target, spec);
        try
        {
            await client.ConnectAsync().Timeout(T(TestTimeouts.Connect));
            return client;
        }
        catch
        {
            await client.DisposeAsync();
            throw;
        }
    }

    private static void CompleteByDefault(ServerNexus nexus)
    {
        nexus.ServerVoidEvent = _ => { };
        nexus.ServerVoidWithParamEvent = (_, _) => { };
        nexus.ServerTaskEvent = _ => ValueTask.CompletedTask;
        nexus.ServerTaskWithParamEvent = (_, _) => ValueTask.CompletedTask;
        nexus.ServerTaskValueEvent = _ => ValueTask.FromResult(0);
        nexus.ServerTaskValueWithParamEvent = (_, _) => ValueTask.FromResult(0);
        nexus.ServerTaskWithCancellationEvent = (_, _) => ValueTask.CompletedTask;
        nexus.ServerTaskWithValueAndCancellationEvent = (_, _, _) => ValueTask.CompletedTask;
        nexus.ServerTaskValueWithCancellationEvent = (_, _) => ValueTask.FromResult(0);
        nexus.ServerTaskValueWithValueAndCancellationEvent = (_, _, _) => ValueTask.FromResult(0);
    }
}
