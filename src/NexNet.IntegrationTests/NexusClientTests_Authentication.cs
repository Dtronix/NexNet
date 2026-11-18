using NexNet.IntegrationTests.Metrics;
using NexNet.IntegrationTests.TestInterfaces;
using NexNet.Messages;
using NexNet.Transports;
using NexNet.Transports.HttpSocket;
using NexNet.Transports.WebSocket;
using NUnit.Framework;
#pragma warning disable VSTHRD200

namespace NexNet.IntegrationTests;

/// <summary>
/// Client authentication token providers (sync and async), the plaintext transport warning and IsSecure.
/// </summary>
internal partial class NexusClientTests
{
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task AuthenticateAsync_ProvidesAuthenticationToken(Type type)
    {
        var clientConfig = CreateClientConfig(type);
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (server, client, _) = CreateServerClient(CreateServerConfig(type), clientConfig);

        await server.StartAsync().Timeout(1);

        clientConfig.AuthenticateAsync = async ct =>
        {
            await Task.Yield();
            return new byte[] { 42 };
        };
        FireOnSend(clientConfig, (_, bytes) =>
        {
            var message = TestSerialization.DeserializeMessage<ClientGreetingMessage>(new ReadOnlySpan<byte>(bytes).Slice(3));
            if (message!.AuthenticationToken!.Span[0] == 42)
                tcs.TrySetResult();
            else
                tcs.TrySetException(new Exception("Client didn't send token"));
        });
        await client.ConnectAsync().Timeout(1);

        await tcs.Task.Timeout(1);
    }

    [TestCase(Type.Tcp)]
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

        await server.StartAsync().Timeout(1);
        await client.ConnectAsync().Timeout(1);
        await client.DisconnectAsync().Timeout(1);
        await client.ConnectAsync().Timeout(1);

        Assert.That(Volatile.Read(ref calls), Is.EqualTo(2));
    }

    [TestCase(Type.Tcp)]
    public async Task AuthenticateAsync_CalledOnReconnect(Type type)
    {
        var clientConfig = CreateClientConfig(type);
        var serverConfig = CreateServerConfig(type);
        var reconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
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

        await server.StartAsync().Timeout(1);
        await client.ConnectAsync().Timeout(1);
        await server.StopAsync();
        await Task.Delay(100);
        await server.StartAsync().Timeout(1);

        await reconnected.Task.Timeout(1);
        Assert.That(Volatile.Read(ref calls), Is.GreaterThanOrEqualTo(2));
    }

    [Test]
    public void AuthenticateAndAuthenticateAsync_BothSet_Throws()
    {
        var clientConfig = CreateClientConfig(Type.Tcp);
        clientConfig.Authenticate = () => new byte[] { 1 };
        clientConfig.AuthenticateAsync = _ => ValueTask.FromResult<Memory<byte>>(new byte[] { 1 });

        Assert.Throws<InvalidOperationException>(() => ClientNexus.CreateClient(clientConfig, new ClientNexus()));
    }

    [TestCase(Type.Tcp)]
    public async Task AuthenticateAsync_BothSetAfterConstruction_FailsConnect(Type type)
    {
        var clientConfig = CreateClientConfig(type);
        var (server, client, _) = CreateServerClient(CreateServerConfig(type), clientConfig);
        await server.StartAsync().Timeout(1);

        clientConfig.Authenticate = () => new byte[] { 1 };
        clientConfig.AuthenticateAsync = _ => ValueTask.FromResult<Memory<byte>>(new byte[] { 1 });

        var result = await client.TryConnectAsync().Timeout(1);
        Assert.That(result.Success, Is.False);
        Assert.That(result.Exception, Is.TypeOf<InvalidOperationException>());
    }

    [TestCase(Type.Tcp)]
    public async Task AuthenticateAsync_Throws_FailsConnectWithoutConnectingTransport(Type type)
    {
        var clientConfig = CreateClientConfig(type);
        var transportConnected = false;
        clientConfig.InternalOnClientConnect = () => transportConnected = true;
        clientConfig.AuthenticateAsync = async _ =>
        {
            await Task.Yield();
            throw new InvalidDataException("token provider failed");
        };
        var (server, client, _) = CreateServerClient(CreateServerConfig(type), clientConfig);
        await server.StartAsync().Timeout(1);

        var result = await client.TryConnectAsync().Timeout(1);

        Assert.That(result.Success, Is.False);
        Assert.That(result.State, Is.EqualTo(ConnectionResult.StateValue.Exception));
        Assert.That(result.DisconnectReason, Is.EqualTo(DisconnectReason.Authentication));
        Assert.That(result.Exception, Is.TypeOf<InvalidDataException>());
        Assert.That(transportConnected, Is.False);
        Assert.That(client.State, Is.EqualTo(ConnectionState.Disconnected));

        await Assert.ThrowsAsync<InvalidDataException>(() => client.ConnectAsync().Timeout(1));
    }

    [TestCase(Type.Tcp)]
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
        await server.StartAsync().Timeout(1);

        var result = await client.TryConnectAsync().Timeout(1);

        Assert.That(result.State, Is.EqualTo(ConnectionResult.StateValue.Exception));
        Assert.That(result.DisconnectReason, Is.EqualTo(DisconnectReason.Authentication));
        Assert.That(result.Exception, Is.TypeOf<TransportException>());
        Assert.That(((TransportException)result.Exception!).Error, Is.EqualTo(TransportError.AuthenticationError));
        await tokenCanceled.Task.Timeout(1);
    }

    [TestCase(Type.Tcp)]
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
        await server.StartAsync().Timeout(1);

        using var cts = new CancellationTokenSource();
        var connect = client.TryConnectAsync(cts.Token);
        await started.Task.Timeout(1);
        await cts.CancelAsync();
        var result = await connect.Timeout(1);

        Assert.That(result.State, Is.EqualTo(ConnectionResult.StateValue.Canceled));
        Assert.That(result.Exception, Is.InstanceOf<OperationCanceledException>());
    }

    [TestCase(Type.Uds, true)]
    [TestCase(Type.Tcp, false)]
    [TestCase(Type.TcpTls, true)]
    [TestCase(Type.Quic, true)]
    [TestCase(Type.WebSocket, false)]
    [TestCase(Type.HttpSocket, false)]
    public async Task IsSecure_ReflectsTransport(Type type, bool expected)
    {
        var (server, client, _) = CreateServerClient(CreateServerConfig(type), CreateClientConfig(type));

        Assert.That(client.IsSecure, Is.EqualTo(expected));
        Assert.That(((INexusClient)client).IsSecure, Is.EqualTo(expected));

        await server.StartAsync().Timeout(1);
        await client.ConnectAsync().Timeout(1);

        var snapshot = client.GetSnapshot();
        Assert.That(snapshot, Is.Not.Null);
        Assert.That(snapshot!.IsSecure, Is.EqualTo(expected));
    }

    [TestCase("wss://localhost/nexus", true)]
    [TestCase("WSS://localhost/nexus", true)]
    [TestCase("ws://localhost/nexus", false)]
    public void IsSecure_WebSocketUrlScheme(string url, bool expected)
    {
        var client = ClientNexus.CreateClient(new WebSocketClientConfig { Url = new Uri(url) }, new ClientNexus());
        Assert.That(client.IsSecure, Is.EqualTo(expected));
    }

    [TestCase("https://localhost/nexus", true)]
    [TestCase("http://localhost/nexus", false)]
    public void IsSecure_HttpSocketUrlScheme(string url, bool expected)
    {
        var client = ClientNexus.CreateClient(new HttpSocketClientConfig { Url = new Uri(url) }, new ClientNexus());
        Assert.That(client.IsSecure, Is.EqualTo(expected));
    }

    [TestCase(Type.Tcp, "(tcp)")]
    [TestCase(Type.WebSocket, "(ws://)")]
    [TestCase(Type.HttpSocket, "(http://)")]
    public async Task PlaintextTransport_LogsWarningOncePerClient(Type type, string transport)
    {
        var logger = new CapturingLogger();
        var clientConfig = CreateClientConfigWithLog(type, logger);
        var (server, client, _) = CreateServerClient(CreateServerConfig(type), clientConfig);
        await server.StartAsync().Timeout(1);

        await client.ConnectAsync().Timeout(1);
        await client.DisconnectAsync().Timeout(1);
        await client.ConnectAsync().Timeout(1);

        var warnings = logger.Warnings.Where(w => w.Contains("unencrypted transport")).ToList();
        Assert.That(warnings, Has.Count.EqualTo(1));
        Assert.That(warnings[0], Is.EqualTo(
            $"NexNet: unencrypted transport {transport}; credentials and data are visible on the network."));
    }

    [TestCase(Type.Tcp)]
    public async Task PlaintextTransport_LogsWarningPerClientInstance(Type type)
    {
        var logger = new CapturingLogger();
        var (server, client1, _) = CreateServerClient(CreateServerConfig(type), CreateClientConfigWithLog(type, logger));
        var client2 = ClientNexus.CreateClient(CreateClientConfigWithLog(type, logger), new ClientNexus());
        await server.StartAsync().Timeout(1);

        await client1.ConnectAsync().Timeout(1);
        await client2.ConnectAsync().Timeout(1);
        await client2.DisposeAsync();

        Assert.That(logger.Warnings.Count(w => w.Contains("unencrypted transport")), Is.EqualTo(2));
    }

    [TestCase(Type.Uds)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.Quic)]
    public async Task SecureTransport_DoesNotLogPlaintextWarning(Type type)
    {
        var logger = new CapturingLogger();
        var (server, client, _) = CreateServerClient(CreateServerConfig(type), CreateClientConfigWithLog(type, logger));
        await server.StartAsync().Timeout(1);

        await client.ConnectAsync().Timeout(1);

        Assert.That(logger.Warnings.Any(w => w.Contains("unencrypted transport")), Is.False);
    }
}
