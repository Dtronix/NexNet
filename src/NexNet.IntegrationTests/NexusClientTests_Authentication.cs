using NexNet.IntegrationTests.Metrics;
using NexNet.IntegrationTests.TestInterfaces;
using NexNet.Interop;
using NexNet.Logging;
using NexNet.Messages;
using NexNet.TestNexus.Interop;
using NexNet.Transports;
using NexNet.Transports.HttpSocket;
using NexNet.Transports.WebSocket;
#pragma warning disable VSTHRD200

namespace NexNet.IntegrationTests;

/// <summary>
/// Client authentication token providers (sync and async), the plaintext transport warning and IsSecure.
/// </summary>
internal partial class NexusClientTests
{
    [Test]
    [Transports(Except = new[] { Type.Quic })]
    public async Task AuthenticateAsync_ProvidesAuthenticationToken(Type type)
    {
        var clientConfig = CreateClientConfig(type);
        var serverConfig = CreateServerConfig(type);
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (server, client, _) = CreateServerClient(serverConfig, clientConfig);

        await server.StartAsync().Timeout(T(TestTimeouts.Connect));

        clientConfig.AuthenticateAsync = async ct =>
        {
            await Task.Yield();
            return new byte[] { 42 };
        };
        ServerTaps.OnReceive(serverConfig, (_, frame) =>
        {
            if (frame.Type != MessageType.ClientGreeting)
                return;

            var message = TestSerialization.DeserializeMessage<ClientGreetingMessage>(frame.Body);
            if (message!.AuthenticationToken!.Span[0] == 42)
                tcs.TrySetResult();
            else
                tcs.TrySetException(new Exception("Client didn't send token"));
        });
        await client.ConnectAsync().Timeout(T(TestTimeouts.Connect));

        await tcs.Task.Timeout(T(1));
    }

    [Test]
    [Transports(Type.Tcp)]
    public async Task AuthenticateAsync_CalledOnEveryConnect(Type type)
    {
        var clientConfig = CreateClientConfig(type);
        var calls = 0;
        clientConfig.AuthenticateAsync = _ =>
        {
            Interlocked.Increment(ref calls);
            return ValueTask.FromResult<Memory<byte>>(new byte[] { 1 });
        };
        var (server, client, _) = CreateServerClient(CreateServerConfig(type), clientConfig);

        await server.StartAsync().Timeout(T(TestTimeouts.Connect));
        await client.ConnectAsync().Timeout(T(TestTimeouts.Connect));
        await client.DisconnectAsync().Timeout(T(1));
        await client.ConnectAsync().Timeout(T(TestTimeouts.Connect));

        await Assert.That(Volatile.Read(ref calls)).IsEqualTo(2);
    }

    [Test]
    [Transports(Type.Tcp)]
    public async Task AuthenticateAsync_CalledOnReconnect(Type type)
    {
        var clientConfig = CreateClientConfig(type);
        var serverConfig = CreateServerConfig(type);
        var reconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reconnecting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        clientConfig.ReconnectionPolicy = new DefaultReconnectionPolicy(new[] { TimeSpan.FromMilliseconds(20) });
        clientConfig.AuthenticateAsync = _ =>
        {
            Interlocked.Increment(ref calls);
            return ValueTask.FromResult<Memory<byte>>(new byte[] { 1 });
        };
        serverConfig.InternalNoLingerOnShutdown = true;
        serverConfig.InternalForceDisableSendingDisconnectSignal = true;
        var (server, client, clientNexus) = CreateServerClient(serverConfig, clientConfig);
        clientNexus.OnConnectedEvent = (_, isReconnected) =>
        {
            if (isReconnected)
                reconnected.TrySetResult();
            return ValueTask.CompletedTask;
        };
        clientNexus.OnReconnectingEvent = _ =>
        {
            reconnecting.TrySetResult();
            return ValueTask.CompletedTask;
        };

        await server.StartAsync().Timeout(T(TestTimeouts.Connect));
        await client.ConnectAsync().Timeout(T(TestTimeouts.Connect));
        await server.StopAsync();
        await reconnecting.Task.Timeout(T(1));
        await server.StartAsync().Timeout(T(TestTimeouts.Connect));

        await reconnected.Task.Timeout(T(1));
        await Assert.That(Volatile.Read(ref calls)).IsGreaterThanOrEqualTo(2);
    }

    [Test]
    public async Task AuthenticateAndAuthenticateAsync_BothSet_Throws()
    {
        var clientConfig = CreateClientConfig(Type.Tcp);
        clientConfig.Authenticate = () => new byte[] { 1 };
        clientConfig.AuthenticateAsync = _ => ValueTask.FromResult<Memory<byte>>(new byte[] { 1 });

        Assert.ThrowsExactly<InvalidOperationException>(() => ClientNexus.CreateClient(clientConfig, new ClientNexus()));
    }

    [Test]
    [Transports(Type.Tcp)]
    public async Task AuthenticateAsync_BothSetAfterConstruction_FailsConnect(Type type)
    {
        var clientConfig = CreateClientConfig(type);
        var (server, client, _) = CreateServerClient(CreateServerConfig(type), clientConfig);
        await server.StartAsync().Timeout(T(TestTimeouts.Connect));

        clientConfig.Authenticate = () => new byte[] { 1 };
        clientConfig.AuthenticateAsync = _ => ValueTask.FromResult<Memory<byte>>(new byte[] { 1 });

        var result = await client.TryConnectAsync().Timeout(T(TestTimeouts.Connect));
        await Assert.That(result.Success).IsFalse();
        await Assert.That(result.Exception).IsTypeOf<InvalidOperationException>();
    }

    [Test]
    [Transports(Type.Tcp)]
    public async Task AuthenticateAsync_Throws_FailsConnectWithoutConnectingTransport(Type type)
    {
        var clientConfig = CreateClientConfig(type);
        var serverConfig = CreateServerConfig(type);
        var sessions = 0;
        ServerTaps.OnSessionCreated(serverConfig, _ => Interlocked.Increment(ref sessions));
        clientConfig.AuthenticateAsync = async _ =>
        {
            await Task.Yield();
            throw new InvalidDataException("token provider failed");
        };
        var (server, client, _) = CreateServerClient(serverConfig, clientConfig);
        await server.StartAsync().Timeout(T(TestTimeouts.Connect));

        var result = await client.TryConnectAsync().Timeout(T(TestTimeouts.Connect));

        await Assert.That(result.Success).IsFalse();
        await Assert.That(result.State).IsEqualTo(ConnectionResult.StateValue.Exception);
        await Assert.That(result.DisconnectReason).IsEqualTo(DisconnectReason.Authentication);
        await Assert.That(result.Exception).IsTypeOf<InvalidDataException>();
        await WaitForState(client, ConnectionState.Disconnected);

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => client.ConnectAsync().Timeout(T(TestTimeouts.Connect)));

        // Barrier: a working client connects after the failed attempts, so the server has accepted any connection
        // they made.
        var (healthy, _) = CreateClient(CreateClientConfig(type));
        await healthy.ConnectAsync().Timeout(T(TestTimeouts.Connect));
        await Assert.That(Volatile.Read(ref sessions)).IsEqualTo(1).Because("The failed attempts must not connect the transport.");
    }

    [Test]
    [Transports(Type.Tcp)]
    public async Task AuthenticateAsync_ExceedsHandshakeTimeout_FailsConnect(Type type)
    {
        var clientConfig = CreateClientConfig(type);
        clientConfig.HandshakeTimeout = 100;
        var tokenCanceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        clientConfig.AuthenticateAsync = async ct =>
        {
            ct.Register(() => tokenCanceled.TrySetResult());
            // Ignores its token: the client must still give up at HandshakeTimeout.
            await Task.Delay(Timeout.Infinite, CancellationToken.None);
            return Memory<byte>.Empty;
        };
        var (server, client, _) = CreateServerClient(CreateServerConfig(type), clientConfig);
        await server.StartAsync().Timeout(T(TestTimeouts.Connect));

        var result = await client.TryConnectAsync().Timeout(T(TestTimeouts.Connect));

        await Assert.That(result.State).IsEqualTo(ConnectionResult.StateValue.Exception);
        await Assert.That(result.DisconnectReason).IsEqualTo(DisconnectReason.Authentication);
        await Assert.That(result.Exception).IsTypeOf<TransportException>();
        await Assert.That(((TransportException)result.Exception!).Error).IsEqualTo(TransportError.AuthenticationError);
        await tokenCanceled.Task.Timeout(T(1));
    }

    [Test]
    [Transports(Type.Tcp)]
    public async Task AuthenticateAsync_ConnectCanceled_ReturnsCanceled(Type type)
    {
        var clientConfig = CreateClientConfig(type);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        clientConfig.AuthenticateAsync = async ct =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return Memory<byte>.Empty;
        };
        var (server, client, _) = CreateServerClient(CreateServerConfig(type), clientConfig);
        await server.StartAsync().Timeout(T(TestTimeouts.Connect));

        using var cts = new CancellationTokenSource();
        var connect = client.TryConnectAsync(cts.Token);
        await started.Task.Timeout(T(1));
        await cts.CancelAsync();
        var result = await connect.Timeout(T(1));

        await Assert.That(result.State).IsEqualTo(ConnectionResult.StateValue.Canceled);
        await Assert.That(result.Exception).IsAssignableTo<OperationCanceledException>();
    }

    /// <summary>
    /// A connect canceled while its authenticate callback hangs ends canceled on every language, well before the
    /// handshake timeout: ConnectAsync fails with kind Canceled (C# <see cref="TaskCanceledException"/>, Java
    /// <c>CancellationException</c>, JS <c>AbortError</c>), TryConnectAsync returns false, and the client is Disconnected.
    /// </summary>
    [Test]
    [ClientMatrix(Transports = [ClientTransport.Tcp, ClientTransport.WebSocket])]
    public async Task Connect_CanceledDuringAuthenticate_EndsCanceled(ClientTarget target)
    {
        await using var server = await StartServer();
        await using var client = await CreateClient(target, server, spec =>
            spec.Authenticate = new AuthenticateScript { Hang = true });

        // Canceled by the test's token once the connect is running.
        using (var cts = new CancellationTokenSource())
        {
            var connect = client.ConnectAsync(cts.Token);
            await client.WaitForStateAsync(ConnectionState.Connecting, T(1));
            await cts.CancelAsync();
            await Assert.That(async () => await connect.Timeout(T(1))).ThrowsExactly<TaskCanceledException>();
        }

        await AssertClientState(client, ConnectionState.Disconnected);

        // Canceled by a host-side timer. The default handshake timeout is 15 s: failing well before it shows the
        // cancellation ended the attempt.
        var watch = System.Diagnostics.Stopwatch.StartNew();
        await Assert.That(await client.TryConnectAsync(RemoteCancel.After(200)).Timeout(T(TestTimeouts.Connect))).IsFalse();
        await Assert.That(watch.Elapsed).IsLessThan(TimeSpan.FromSeconds(10));
        await AssertClientState(client, ConnectionState.Disconnected);
    }

    [Test]
    [Arguments(Type.Uds, true)]
    [Arguments(Type.Tcp, false)]
    [Arguments(Type.TcpTls, true)]
    [Arguments(Type.Quic, true)]
    [Arguments(Type.WebSocket, false)]
    [Arguments(Type.HttpSocket, false)]
    public async Task IsSecure_ReflectsTransport(Type type, bool expected)
    {
        var (server, client, _) = CreateServerClient(CreateServerConfig(type), CreateClientConfig(type));

        await Assert.That(client.IsSecure).IsEqualTo(expected);
        await Assert.That(((INexusClient)client).IsSecure).IsEqualTo(expected);

        await server.StartAsync().Timeout(T(TestTimeouts.Connect));
        await client.ConnectAsync().Timeout(T(TestTimeouts.Connect));

        var snapshot = client.GetSnapshot();
        await Assert.That(snapshot).IsNotNull();
        await Assert.That(snapshot!.IsSecure).IsEqualTo(expected);
    }

    [Test]
    [Arguments("wss://localhost/nexus", true)]
    [Arguments("WSS://localhost/nexus", true)]
    [Arguments("ws://localhost/nexus", false)]
    public async Task IsSecure_WebSocketUrlScheme(string url, bool expected)
    {
        var client = ClientNexus.CreateClient(new WebSocketClientConfig { Url = new Uri(url) }, new ClientNexus());
        await Assert.That(client.IsSecure).IsEqualTo(expected);
    }

    [Test]
    [Arguments("https://localhost/nexus", true)]
    [Arguments("http://localhost/nexus", false)]
    public async Task IsSecure_HttpSocketUrlScheme(string url, bool expected)
    {
        var client = ClientNexus.CreateClient(new HttpSocketClientConfig { Url = new Uri(url) }, new ClientNexus());
        await Assert.That(client.IsSecure).IsEqualTo(expected);
    }

    /// <summary>The transport text of the plaintext warning: C# <c>ConfigBase.TransportName</c>, or the URL scheme.</summary>
    private static string PlaintextName(ClientTransport transport) => transport switch
    {
        ClientTransport.Tcp => "tcp",
        ClientTransport.WebSocket => "ws://",
        ClientTransport.HttpSocket => "http://",
        _ => throw new ArgumentOutOfRangeException(nameof(transport), transport, null),
    };

    /// <summary>Logs the client's warnings to <paramref name="logger"/> (control protocol §7.4 on language hosts).</summary>
    private static Action<ClientSpec> LogWarnings(CapturingLogger logger) => spec =>
    {
        spec.Logger = logger;
        spec.LogLevel = NexusLogLevel.Warning;
    };

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Tcp, ClientTransport.WebSocket, ClientTransport.HttpSocket])]
    public async Task PlaintextTransport_LogsWarningOncePerClient(ClientTarget target)
    {
        var logger = new CapturingLogger();
        await using var server = await StartServer();
        await using var client = await Connect(target, server, LogWarnings(logger));

        await client.DisconnectAsync().Timeout(T(1));
        await client.ConnectAsync().Timeout(T(TestTimeouts.Connect));
        await client.Proxy<IInteropServerNexus>().Ping().Timeout(T(1));

        var warnings = logger.Warnings.Where(w => w.Contains("unencrypted transport")).ToList();
        await Assert.That(warnings).Count().IsEqualTo(1);
        await Assert.That(warnings[0]).IsEqualTo(
            $"NexNet: unencrypted transport ({PlaintextName(target.Transport)}); credentials and data are visible on the network.");
        await MatrixTestBase.EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Tcp, ClientTransport.WebSocket])]
    public async Task PlaintextTransport_LogsWarningPerClientInstance(ClientTarget target)
    {
        var logger = new CapturingLogger();
        await using var server = await StartServer();
        await using var client1 = await Connect(target, server, LogWarnings(logger));
        await using (var client2 = await Connect(target, server, LogWarnings(logger)))
        {
            await client2.Proxy<IInteropServerNexus>().Ping().Timeout(T(1));
            await MatrixTestBase.EndAsync(client2);
        }

        await client1.Proxy<IInteropServerNexus>().Ping().Timeout(T(1));
        await Assert.That(logger.Warnings.Count(w => w.Contains("unencrypted transport"))).IsEqualTo(2);
        await MatrixTestBase.EndAsync(client1);
    }

    [Test]
    public async Task CustomTransport_DefaultsToPlaintext_WarnsWithTypeName()
    {
        var logger = new CapturingLogger();
        var client = ClientNexus.CreateClient(new CustomPlaintextClientConfig { Logger = logger }, new ClientNexus());

        await Assert.That(client.IsSecure).IsFalse();

        var result = await client.TryConnectAsync().Timeout(T(TestTimeouts.Connect));
        await Assert.That(result.Success).IsFalse();

        var warnings = logger.Warnings.Where(w => w.Contains("unencrypted transport")).ToList();
        await Assert.That(warnings).IsEquivalentTo(new[]
        {
            $"NexNet: unencrypted transport ({nameof(CustomPlaintextClientConfig)}); credentials and data are visible on the network."
        }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task CustomTransport_OverridingIsSecureTransport_DoesNotWarn()
    {
        var logger = new CapturingLogger();
        var client = ClientNexus.CreateClient(new CustomSecureClientConfig { Logger = logger }, new ClientNexus());

        await Assert.That(client.IsSecure).IsTrue();

        await client.TryConnectAsync().Timeout(T(TestTimeouts.Connect));

        await Assert.That(logger.Warnings.Any(w => w.Contains("unencrypted transport"))).IsFalse();
    }

    /// <summary>A custom transport that does not say whether it is secure. Its connect always fails.</summary>
    private class CustomPlaintextClientConfig : ClientConfig
    {
        protected override ValueTask<ITransport> OnConnectTransport(CancellationToken cancellationToken)
            => throw new TransportException(TransportError.ConnectionRefused, "No custom transport.", null);
    }

    private sealed class CustomSecureClientConfig : CustomPlaintextClientConfig
    {
        protected internal override bool IsSecureTransport => true;
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.TcpTls, ClientTransport.WebSocketTls, ClientTransport.HttpSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public async Task SecureTransport_DoesNotLogPlaintextWarning(ClientTarget target)
    {
        var logger = new CapturingLogger();
        await using var server = await StartServer();
        await using var client = await Connect(target, server, LogWarnings(logger));
        await client.Proxy<IInteropServerNexus>().Ping().Timeout(T(1));

        await Assert.That(logger.Warnings.Any(w => w.Contains("unencrypted transport"))).IsFalse();
        await MatrixTestBase.EndAsync(client);
    }
}
