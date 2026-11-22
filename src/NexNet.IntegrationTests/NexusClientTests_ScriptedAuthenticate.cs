using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Principal;
using System.Text;
using NexNet.Interop;
using NexNet.InteropServer;
using NexNet.Messages;
#pragma warning disable VSTHRD200

namespace NexNet.IntegrationTests;

/// <summary>
/// The asynchronous authenticate callback on every row of the client matrix (<see cref="ClientSpec.Authenticate"/>):
/// its token reaches the server, it runs before every connect and reconnect, a throwing callback fails the connect
/// before the transport connects, and <see cref="ClientSpec.HandshakeTimeoutMs"/> bounds it.
/// </summary>
internal partial class NexusClientTests
{
    /// <summary>Server options that record every greeting token the server receives, as UTF-8 text, and accept it.</summary>
    private static InteropServerOptions RecordTokens(ConcurrentQueue<string> tokens, InteropServerOptions? options = null)
    {
        options ??= new InteropServerOptions();
        options.TokenValidator = (sent, _) =>
        {
            tokens.Enqueue(sent == null ? "" : Encoding.UTF8.GetString(sent.Value.Span));
            return ValueTask.FromResult<IIdentity?>(new DefaultIdentity());
        };
        return options;
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.HttpSocket, ClientTransport.HttpSocketTls, ClientTransport.WebSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public async Task AuthenticateCallback_AsyncToken_ReachesTheServer(ClientTarget target)
    {
        var tokens = new ConcurrentQueue<string>();
        await using var server = await StartServer(RecordTokens(tokens));
        await using var client = await Connect(target, server, spec =>
            spec.Authenticate = new AuthenticateScript { Token = "async-token", DelayMs = 20 });

        await Assert.That(tokens).IsEquivalentTo(new[] { "async-token" });
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Tcp, ClientTransport.WebSocket])]
    public async Task AuthenticateCallback_CalledOnEveryConnect(ClientTarget target)
    {
        var tokens = new ConcurrentQueue<string>();
        await using var server = await StartServer(RecordTokens(tokens));
        await using var client = await CreateClient(target, server, spec =>
            spec.Authenticate = new AuthenticateScript { Token = "t", AppendCallNumber = true });

        await client.ConnectAsync().Timeout(T(TestTimeouts.Connect));
        await client.DisconnectAsync().Timeout(T(1));
        await client.ConnectAsync().Timeout(T(TestTimeouts.Connect));

        await Assert.That(tokens).IsEquivalentTo(new[] { "t1", "t2" }, CollectionOrdering.Matching);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Tcp, ClientTransport.WebSocket])]
    public async Task AuthenticateCallback_CalledOnReconnect(ClientTarget target)
    {
        var tokens = new ConcurrentQueue<string>();
        await using var server = await StartDedicatedServer(RecordTokens(tokens, AbruptStop()));
        await using var client = await Connect(target, server, spec =>
        {
            spec.Authenticate = new AuthenticateScript { Token = "t", AppendCallNumber = true };
            spec.ReconnectDelaysMs = [20];
            spec.ReconnectContinuously = true;
        });
        await WaitForFirstConnected(client);

        var disconnected = client.WhenDisconnectedAsync(T(TestTimeouts.Connect));
        await server.StopAsync();
        await disconnected;
        await server.StartAsync().Timeout(T(TestTimeouts.Connect));
        await WaitForReconnected(client, T(TestTimeouts.Connect));
        await WaitUntil(() => tokens.Count >= 2, T(TestTimeouts.Connect));

        // Attempts made while the server was stopped also call it: the reconnect's token has a later number.
        var received = tokens.ToArray();
        await Assert.That(received[0]).IsEqualTo("t1");
        await Assert.That(received.Length).IsGreaterThanOrEqualTo(2).Because("tokens: " + string.Join(",", received));
        await Assert.That(received[^1]).IsNotEqualTo("t1");
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Tcp, ClientTransport.WebSocket])]
    public async Task AuthenticateCallback_Throws_FailsTheConnectWithoutASession(ClientTarget target)
    {
        var sessions = 0;
        await using var server = await StartServer(new InteropServerOptions
        {
            ConfigureEndpoint = (_, config) => ServerTaps.OnSessionCreated(config, _ => Interlocked.Increment(ref sessions)),
        });
        await using var client = await CreateClient(target, server, spec =>
            spec.Authenticate = new AuthenticateScript { DelayMs = 10, ThrowMessage = "token provider failed" });

        await Assert.That(await client.TryConnectAsync().Timeout(T(TestTimeouts.Connect))).IsFalse();
        await AssertClientState(client, ConnectionState.Disconnected);
        await Assert.That(await client.TryConnectAsync().Timeout(T(TestTimeouts.Connect))).IsFalse();

        // Barrier: a working client connects after the failed attempts, so the server has accepted any connection
        // they made.
        await using var healthy = await Connect(target, server);
        await Assert.That(Volatile.Read(ref sessions)).IsEqualTo(1).Because("A failed callback must not connect the transport.");
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Tcp, ClientTransport.WebSocket])]
    public async Task AuthenticateCallback_ExceedsHandshakeTimeout_FailsTheConnect(ClientTarget target)
    {
        await using var server = await StartServer();
        await using var client = await CreateClient(target, server, spec =>
        {
            spec.Authenticate = new AuthenticateScript { Hang = true };
            spec.HandshakeTimeoutMs = 200;
        });

        // The default handshake timeout is 15 s: failing well before it shows HandshakeTimeoutMs applies.
        var watch = Stopwatch.StartNew();
        await Assert.That(await client.TryConnectAsync().Timeout(T(TestTimeouts.Connect))).IsFalse();
        await Assert.That(watch.Elapsed).IsLessThan(TimeSpan.FromSeconds(10));
        await AssertClientState(client, ConnectionState.Disconnected);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Tcp, ClientTransport.WebSocket])]
    public async Task AuthenticateCallback_WithinHandshakeTimeout_Connects(ClientTarget target)
    {
        var tokens = new ConcurrentQueue<string>();
        await using var server = await StartServer(RecordTokens(tokens));
        await using var client = await Connect(target, server, spec =>
        {
            spec.Authenticate = new AuthenticateScript { Token = "slow", DelayMs = 100 };
            spec.HandshakeTimeoutMs = 5000;
        });

        await Assert.That(tokens).IsEquivalentTo(new[] { "slow" });
    }

    private static async Task AssertClientState(IClientUnderTest client, ConnectionState state)
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
}
