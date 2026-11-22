using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using NexNet.Diagnostics;
using NexNet.IntegrationTests.Pipes;
using NexNet.IntegrationTests.TestInterfaces;
using NexNet.Logging;
using NexNet.Quic;
using NexNet.RateLimiting;
using NexNet.Transports;
#pragma warning disable VSTHRD200

namespace NexNet.IntegrationTests;

/// <summary>
/// Server start/stop lifecycle: bind failures, restart, and stop latency.
/// </summary>
internal class NexusServerTests_Lifecycle : BaseTests
{
    /// <summary>
    /// A bind failure must leave the server Stopped (not Running without a listener), and a later start, once the
    /// address is free, must work.
    /// </summary>
    [Test]
    [Transports(Type.Uds, Type.Tcp, Type.TcpTls)]
    public async Task BindFailure_LeavesServerStopped_AndLaterStartWorks(Type type)
    {
        var config = CreateServerConfig(type);
        var occupier = CreateServer(config, null);
        var server = CreateServer(config, null);

        await occupier.StartAsync().Timeout(TestTimeouts.Connect);

        var exception = await Assert.ThrowsExactlyAsync<TransportException>(() => server.StartAsync().Timeout(TestTimeouts.Connect));
        await Assert.That(exception!.Error).IsEqualTo(TransportError.AddressInUse);
        await Assert.That(exception.Message).Contains($"'{config.TransportName}'").Because("The exception should name the endpoint that failed to bind.");
        await Assert.That(server.State).IsEqualTo(NexusServerState.Stopped);

        // Raw stop and start, with no port hold (StopServerAsync): the hold belongs to the stopped server, so it would
        // keep the port from the other one, and with nothing between the two calls a hold would close no window.
        await occupier.StopAsync().Timeout(TestTimeouts.Connect);

        await server.StartAsync().Timeout(TestTimeouts.Connect);
        await Assert.That(server.State).IsEqualTo(NexusServerState.Running);

        var (client, _) = CreateClient(CreateClientConfig(type));
        await client.ConnectAsync().Timeout(TestTimeouts.Connect);
        await Assert.That(client.State).IsEqualTo(ConnectionState.Connected);
    }

    [Test]
    public async Task NullListener_LeavesServerStopped()
    {
        var server = ServerNexus.CreateServer(new CustomServerConfig(ServerConnectionMode.Listener), () => null!);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => server.StartAsync());
        await Assert.That(server.State).IsEqualTo(NexusServerState.Stopped);
        await server.DisposeAsync();
    }

    /// <summary>
    /// The rate limiter is disposed on stop; a restarted server must create a new one and keep enforcing it.
    /// </summary>
    [Test]
    [Transports(Type.Uds, Type.Tcp)]
    public async Task RateLimiting_AppliesAfterRestart(Type type)
    {
        var serverConfig = CreateServerConfig(type);
        serverConfig.RateLimiting = new ConnectionRateLimitConfig { MaxConcurrentConnections = 1 };
        var server = CreateServer(serverConfig, null);

        await server.StartAsync().Timeout(TestTimeouts.Connect);
        await Assert.That(server.GetSnapshot().RateLimiter).IsNotNull();

        // The port is held while the server is stopped, so no server of another test process can take it.
        await StopServerAsync(server).Timeout(TestTimeouts.Connect);
        await Assert.That(server.GetSnapshot().RateLimiter).IsNull();

        await StartServerAsync(server).Timeout(TestTimeouts.Connect);
        await Assert.That(server.GetSnapshot().RateLimiter).IsNotNull();

        var (client1, _) = CreateClient(CreateClientConfig(type));
        await client1.ConnectAsync().Timeout(TestTimeouts.Connect);
        await Assert.That(client1.State).IsEqualTo(ConnectionState.Connected);

        var (client2, _) = CreateClient(CreateClientConfig(type));
        var result = await client2.TryConnectAsync().Timeout(2);
        await Assert.That(result.Success).IsFalse().Because("The second connection should be rejected by the rate limiter.");
        await Assert.That(client2.State).IsNotEqualTo(ConnectionState.Connected);
    }

    /// <summary>
    /// Stop disconnects sessions in parallel, so each session's disconnect delay overlaps instead of adding up
    /// (200 sessions x 200 ms would take 40 s sequentially).
    /// </summary>
    [Test]
    [NotInParallel]
    public async Task Stop_With200Sessions_CompletesQuickly()
    {
        const int sessionCount = 200;
        var serverConfig = CreateServerConfig(Type.Tcp, BasePipeTests.LogMode.None);
        serverConfig.DisconnectDelay = 200;
        serverConfig.MaxPendingHandshakes = 1000;

        var connected = 0;
        var allConnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = CreateServer(serverConfig, nexus => nexus.OnConnectedEvent = _ =>
        {
            if (Interlocked.Increment(ref connected) == sessionCount)
                allConnected.TrySetResult();
            return ValueTask.CompletedTask;
        });
        await server.StartAsync().Timeout(TestTimeouts.Connect);

        var clients = new List<NexusClient<ClientNexus, ClientNexus.ServerProxy>>(sessionCount);
        for (var i = 0; i < sessionCount; i++)
            clients.Add(CreateClient(CreateClientConfig(Type.Tcp, BasePipeTests.LogMode.None)).client);

        foreach (var batch in clients.Chunk(25))
            await Task.WhenAll(batch.Select(c => c.ConnectAsync())).Timeout(10);

        await allConnected.Task.Timeout(10);

        var stopwatch = Stopwatch.StartNew();
        await server.StopAsync().Timeout(10);
        stopwatch.Stop();

        await Assert.That(server.State).IsEqualTo(NexusServerState.Stopped);
        await Assert.That(stopwatch.Elapsed).IsLessThan(TimeSpan.FromSeconds(5));

        await Task.WhenAll(clients.Select(c => c.DisconnectedTask)).Timeout(5);
    }

    /// <summary>
    /// The accept loop of a stopped run can see its closed listener fail only after the next start has finished.
    /// That late failure belongs to the old run: it must not fault the endpoint of the new run or stop the restarted
    /// server (S16; seen on Linux, where the cancelled accept surfaces late).
    /// </summary>
    [Test]
    [Transports(Type.Tcp, Type.Uds)]
    public async Task FastStopStartCycles_ServerKeepsRunningAfterLastStart(Type type)
    {
        const int cycles = 200;
        var server = CreateServer(CreateServerConfig(type, BasePipeTests.LogMode.None),
            nexus => nexus.ServerTaskValueEvent = _ => ValueTask.FromResult(42));

        await server.StartAsync().Timeout(TestTimeouts.Connect);

        // Raw stop and start, with no port hold (StopServerAsync/StartServerAsync): nothing runs between the two calls,
        // so a hold would close no window, and its binds would give the stopped run's accept loop time to fail before
        // the next start, which is the race this test is for.
        for (var i = 0; i < cycles; i++)
        {
            await server.StopAsync().Timeout(TestTimeouts.Connect);
            await server.StartAsync().Timeout(TestTimeouts.Connect);
        }

        var (client, _) = CreateClient(CreateClientConfig(type));
        await client.ConnectAsync().Timeout(TestTimeouts.Connect);
        await Assert.That(await client.Proxy.ServerTaskValue().Timeout(1)).IsEqualTo(42);

        // Late failures of the stopped runs' accept loops would stop the server in this window.
        await Task.Delay(200);

        await Assert.That(server.State).IsEqualTo(NexusServerState.Running);
        foreach (var endpoint in server.GetSnapshot().Endpoints)
            await Assert.That(endpoint.State).IsEqualTo(NexusEndpointState.Running);
        await Assert.That(client.State).IsEqualTo(ConnectionState.Connected);
        await Assert.That(await client.Proxy.ServerTaskValue().Timeout(1)).IsEqualTo(42);
    }

    [Test]
    [Transports(Type.Tcp)]
    public async Task DisposeAsync_WhileRunning_StopsAndDisposes(Type type)
    {
        var server = CreateServer(CreateServerConfig(type), null);
        await server.StartAsync().Timeout(TestTimeouts.Connect);
        var (client, _) = CreateClient(CreateClientConfig(type));
        await client.ConnectAsync().Timeout(TestTimeouts.Connect);

        await server.DisposeAsync().AsTask().Timeout(2);

        await Assert.That(server.State).IsEqualTo(NexusServerState.Disposed);
        await client.DisconnectedTask.Timeout(2);
        await server.StoppedTask!.Timeout(1);
    }

    /// <summary>
    /// A fatal accept error (here: the socket is bound but not listening) must surface as the original
    /// exception, never as a NullReferenceException from the never-assigned client socket.
    /// </summary>
    [Test]
    public async Task SocketListener_FatalAcceptError_RethrowsOriginalException()
    {
        using var socket = CreateBoundNotListeningSocket();
        var listener = new SocketTransportListener(new CustomServerConfig(ServerConnectionMode.Listener), socket);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () => await listener.AcceptTransportAsync(CancellationToken.None));
        await listener.CloseAsync(false);
    }

    [Test]
    public async Task SocketListener_ClosedSocket_DoesNotThrowNullReference()
    {
        var socket = CreateBoundNotListeningSocket();
        var listener = new SocketTransportListener(new CustomServerConfig(ServerConnectionMode.Listener), socket);
        await listener.CloseAsync(false);

        var exception = await Assert.ThrowsAsync<Exception>(async () => await listener.AcceptTransportAsync(CancellationToken.None));
        await Assert.That(exception).IsNotAssignableTo<NullReferenceException>();
    }

    [Test]
    public async Task SocketListener_CanceledAccept_ReturnsNull()
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        socket.Listen();
        var listener = new SocketTransportListener(new CustomServerConfig(ServerConnectionMode.Listener), socket);

        using var cts = new CancellationTokenSource(50);
        var transport = await listener.AcceptTransportAsync(cts.Token).AsTask().Timeout(1);
        await Assert.That(transport).IsNull();
        await listener.CloseAsync(false);
    }

    [Test]
    [Arguments(SocketError.ConnectionReset, true)]
    [Arguments(SocketError.ConnectionAborted, true)]
    [Arguments(SocketError.NetworkReset, true)]
    [Arguments(SocketError.InvalidArgument, false)]
    [Arguments(SocketError.OperationAborted, false)]
    [Arguments(SocketError.NotSocket, false)]
    [Arguments(SocketError.TooManyOpenSockets, false)]
    public async Task SocketListener_TransientAcceptErrors(SocketError error, bool transient)
    {
        await Assert.That(SocketTransportListener.IsTransientAcceptError(error)).IsEqualTo(transient);
    }

    /// <summary>
    /// A fatal accept error while Running faults the endpoint (and, as the only endpoint, stops the server) with
    /// the original exception logged rather than a NullReferenceException.
    /// </summary>
    [Test]
    public async Task SocketListener_FatalAcceptError_FaultsEndpointWithOriginalException()
    {
        var logger = new ExceptionCapturingLogger();
        var config = new FatalAcceptServerConfig { Logger = logger };
        var server = ServerNexus.CreateServer(config, () => null!);

        await server.StartAsync().Timeout(TestTimeouts.Connect);

        // The fault stops the server on its own: wait through Running and the transient Stopping state.
        var sw = Stopwatch.StartNew();
        while (server.State != NexusServerState.Stopped && sw.Elapsed < TimeSpan.FromSeconds(5))
            await Task.Delay(10);

        await Assert.That(server.State).IsEqualTo(NexusServerState.Stopped);
        await Assert.That(logger.Exceptions.OfType<NullReferenceException>()).IsEmpty();
        await Assert.That(logger.Exceptions.OfType<InvalidOperationException>()).IsNotEmpty().Because("The endpoint fault should log the original accept exception.");
        await server.DisposeAsync();
    }

    /// <summary>
    /// TcpTls variant of <see cref="SocketListener_FatalAcceptError_RethrowsOriginalException"/>.
    /// </summary>
    [Test]
    public async Task TcpTlsListener_FatalAcceptError_RethrowsOriginalException()
    {
        using var socket = CreateBoundNotListeningSocket();
        var listener = new TcpTlsTransportListener((TcpTlsServerConfig)CreateServerConfig(Type.TcpTls), socket);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () => await listener.AcceptTransportAsync(CancellationToken.None));
        await listener.CloseAsync(false);
    }

    [Test]
    public async Task TcpTlsListener_ClosedSocket_DoesNotThrowNullReference()
    {
        var socket = CreateBoundNotListeningSocket();
        var listener = new TcpTlsTransportListener((TcpTlsServerConfig)CreateServerConfig(Type.TcpTls), socket);
        await listener.CloseAsync(false);

        var exception = await Assert.ThrowsAsync<Exception>(async () => await listener.AcceptTransportAsync(CancellationToken.None));
        await Assert.That(exception).IsNotAssignableTo<NullReferenceException>();
    }

    [Test]
    public async Task TcpTlsListener_CanceledAccept_ReturnsNull()
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        socket.Listen();
        var listener = new TcpTlsTransportListener((TcpTlsServerConfig)CreateServerConfig(Type.TcpTls), socket);

        using var cts = new CancellationTokenSource(50);
        var transport = await listener.AcceptTransportAsync(cts.Token).AsTask().Timeout(1);
        await Assert.That(transport).IsNull();
        await listener.CloseAsync(false);
    }

    /// <summary>
    /// A connection that fails the TLS handshake (here: plain garbage instead of a ClientHello) is rejected and
    /// the listener returns null instead of throwing.
    /// </summary>
    [Test]
    public async Task TcpTlsListener_FailedHandshake_ReturnsNull()
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        socket.Listen();
        var listener = new TcpTlsTransportListener((TcpTlsServerConfig)CreateServerConfig(Type.TcpTls), socket);

        using var badClient = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await badClient.ConnectAsync(socket.LocalEndPoint!).Timeout(TestTimeouts.Connect);
        await badClient.SendAsync(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 }).Timeout(1);

        var transport = await listener.AcceptTransportAsync(CancellationToken.None).AsTask().Timeout(5);
        await Assert.That(transport).IsNull();
        await listener.CloseAsync(false);
    }

    /// <summary>
    /// Clients that fail the handshake (a non-TLS client, or a client that rejects the server certificate) must not
    /// stop the server, and a good client must still connect afterwards.
    /// </summary>
    [Test]
    [Transports(Type.TcpTls, Type.Quic)]
    public async Task FailedClientHandshake_DoesNotStopServer(Type type)
    {
        var logger = new ExceptionCapturingLogger();
        var serverConfig = CreateServerConfig(type);
        serverConfig.Logger = logger;
        var server = CreateServer(serverConfig, null);
        await server.StartAsync().Timeout(TestTimeouts.Connect);

        if (type == Type.TcpTls)
        {
            // Plain bytes instead of a TLS ClientHello.
            using var rawClient = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            await rawClient.ConnectAsync(((TcpTlsServerConfig)serverConfig).EndPoint).Timeout(1);
            await rawClient.SendAsync(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 }).Timeout(1);
            var buffer = new byte[16];
            // The server closes the connection once the handshake fails.
            try
            {
                await rawClient.ReceiveAsync(buffer).Timeout(5);
            }
            catch (SocketException)
            {
                // Reset by the server: expected.
            }
        }

        // A client that rejects the server certificate fails its handshake.
        var badClientConfig = CreateClientConfig(type);
        var sslOptions = type == Type.Quic
            ? ((QuicClientConfig)badClientConfig).SslClientAuthenticationOptions
            : ((TcpTlsClientConfig)badClientConfig).SslClientAuthenticationOptions;
        sslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => false;
        var (badClient, _) = CreateClient(badClientConfig);
        var badResult = await badClient.TryConnectAsync().Timeout(5);
        await Assert.That(badResult.Success).IsFalse();

        // Give the listener time to observe the failed handshake.
        await Task.Delay(200);
        await Assert.That(server.State).IsEqualTo(NexusServerState.Running);

        var (client, _) = CreateClient(CreateClientConfig(type));
        await client.ConnectAsync().Timeout(5);
        await Assert.That(client.State).IsEqualTo(ConnectionState.Connected);
        await Assert.That(server.State).IsEqualTo(NexusServerState.Running);
        await Assert.That(logger.Exceptions.OfType<NullReferenceException>()).IsEmpty();
    }

    private static Socket CreateBoundNotListeningSocket()
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return socket;
    }

    private sealed class FatalAcceptServerConfig() : ServerConfig(ServerConnectionMode.Listener)
    {
        protected override ValueTask<ITransportListener?> OnCreateServerListener(CancellationToken cancellationToken)
            => new(new SocketTransportListener(this, CreateBoundNotListeningSocket()));
    }

    private sealed class ExceptionCapturingLogger : INexusLogger
    {
        private readonly ConcurrentQueue<Exception> _exceptions;

        public ExceptionCapturingLogger() : this(new ConcurrentQueue<Exception>(), null)
        {
        }

        private ExceptionCapturingLogger(ConcurrentQueue<Exception> exceptions, string? pathSegment)
        {
            _exceptions = exceptions;
            PathSegment = pathSegment;
        }

        public IEnumerable<Exception> Exceptions => _exceptions;
        public NexusLogBehaviors Behaviors { get; set; }
        public string FormattedPath => PathSegment ?? "";
        public string? PathSegment { get; set; }

        public void Log(NexusLogLevel logLevel, string? category, Exception? exception, string message)
        {
            if (exception != null)
                _exceptions.Enqueue(exception);
        }

        public INexusLogger CreateLogger(string? pathSegment = null) => new ExceptionCapturingLogger(_exceptions, pathSegment);
    }
}