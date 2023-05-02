using System.Net.Sockets;
using MemoryPack;
using NexNet.IntegrationTests.TestInterfaces;
using NexNet.Messages;
using NexNet.Transports;
using NUnit.Framework;

namespace NexNet.IntegrationTests;

internal partial class NexNetClientTests : BaseTests
{
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    public async Task HubFiresOnConnected(Type type)
    {
        var tcs = new TaskCompletionSource();
        var (server, serverHub, client, clientHub) = CreateServerClient(
            CreateServerConfig(type, false),
            CreateClientConfig(type, false));

        clientHub.OnConnectedEvent = async (hub, _) => tcs.SetResult();

        server.Start();
        await client.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(1));

        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    public async Task ConnectsToServer(Type type)
    {
        var clientConfig = CreateClientConfig(type, false);
        var tcs = new TaskCompletionSource();
        var (server, serverHub, client, clientHub) = CreateServerClient(
            CreateServerConfig(type, false),
            clientConfig);


        clientConfig.InternalOnClientConnect = () => tcs.SetResult();

        server.Start();
        await client.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(1));

        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    public void ClientFailsGracefullyWithNoServer(Type type)
    {
        var clientConfig = CreateClientConfig(type, false);
        var tcs = new TaskCompletionSource();
        var (server, serverHub, client, clientHub) = CreateServerClient(
            CreateServerConfig(type, false),
            clientConfig);

        clientConfig.ConnectionTimeout = 100;

        Assert.ThrowsAsync<SocketException>(() => client.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    public void ClientTimesOutWithNoServer(Type type)
    {
        var clientConfig = CreateClientConfig(type, false);
        var tcs = new TaskCompletionSource();
        var (server, serverHub, client, clientHub) = CreateServerClient(
            CreateServerConfig(type, false),
            clientConfig);

        clientConfig.ConnectionTimeout = 50;

        Assert.ThrowsAsync<SocketException>(() => client.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    public async Task ConnectsAndDisconnectsMultipleTimesFromServer(Type type)
    {
        var (server, serverHub, client, clientHub) = CreateServerClient(
            CreateServerConfig(type, false),
            CreateClientConfig(type, false));

        server.Start();

        for (int i = 0; i < 5; i++)
        {
            await client.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(1));
            await clientHub.ConnectedTCS.Task.WaitAsync(TimeSpan.FromSeconds(1));
            clientHub.ConnectedTCS = new TaskCompletionSource();
            await client.DisconnectAsync().WaitAsync(TimeSpan.FromSeconds(1));
            await clientHub.DisconnectedTCS.Task.WaitAsync(TimeSpan.FromSeconds(1));
            clientHub.DisconnectedTCS = new TaskCompletionSource();
        }
    }

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    public void ConnectTimesOutWithNoServer(Type type)
    {
        var (server, serverHub, client, clientHub) = CreateServerClient(
            CreateServerConfig(type, false),
            CreateClientConfig(type, false));

        Assert.ThrowsAsync<SocketException>(async () => await client.ConnectAsync());
    }

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    public async Task ClientProvidesAuthenticationToken(Type type)
    {
        var clientConfig = CreateClientConfig(type, false);
        var tcs = new TaskCompletionSource();
        var (server, serverHub, client, clientHub) = CreateServerClient(
            CreateServerConfig(type, false),
            clientConfig);

        server.Start();

        clientConfig.Authenticate = () => new byte[] { 123 };
        clientConfig.InternalOnSend= (_, bytes) =>
        {
            var message = MemoryPackSerializer.Deserialize<ClientGreetingMessage>(new ReadOnlySpan<byte>(bytes).Slice(3));

            if (message.AuthenticationToken[0] == 123)
            {
                tcs.SetResult();
                return;
            }
            tcs.SetException(new Exception("Client didn't send token"));
        };
        await client.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(1));

        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    public async Task ClientSendsPing(Type type)
    {
        var clientConfig = CreateClientConfig(type, false);
        clientConfig.PingInterval = 20;
        var tcs = new TaskCompletionSource();

        var (server, serverHub, client, clientHub) = CreateServerClient(
            CreateServerConfig(type, false),
            clientConfig);

        server.Start();

        clientConfig.InternalOnSend = (_, bytes) =>
        {
            if (bytes.Length == 1 && bytes[0] == (int)MessageType.Ping)
                tcs.SetResult();
        };

        await client.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(1));

        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    public async Task ClientResumePingOnDisconnect(Type type)
    {
        var clientConfig = CreateClientConfig(type, false);
        clientConfig.PingInterval = 20;
        var tcs = new TaskCompletionSource();

        var (server, serverHub, client, clientHub) = CreateServerClient(
            CreateServerConfig(type, false),
            clientConfig);

        server.Start();

        await client.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(1));
        await clientHub.ConnectedTCS.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await client.DisconnectAsync().WaitAsync(TimeSpan.FromSeconds(1));


        clientConfig.InternalOnSend = (_, bytes) =>
        {
            if (bytes.Length == 1 && bytes[0] == (int)MessageType.Ping)
                tcs.SetResult();
        };

        await client.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(1));

        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(1));
    }


    private async Task<Task> ClientSendsMessage<T>(Type type, Action<ServerHub> setup, Action<T, TaskCompletionSource> onMessage, Func<NexNetClient<ClientHub, ServerHubProxyImpl>, ValueTask> action)
    {
        var clientConfig = CreateClientConfig(type, false);
        var tcs = new TaskCompletionSource();
        var (server, serverHub, client, clientHub) = CreateServerClient(
            CreateServerConfig(type, false),
            clientConfig);

        setup?.Invoke(serverHub);

        server.Start();

        clientConfig.InternalOnSend = (_, bytes) =>
        {
            try
            {
                var message = MemoryPackSerializer.Deserialize<T>(new ReadOnlySpan<byte>(bytes).Slice(3));
                onMessage(message, tcs);
            }
            catch
            {
                // not a type we care about
            }
        };

        await client.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(1));

        await action.Invoke(client);

        return tcs.Task;
    }


    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    public async Task ReconnectsOnDisconnect(Type type)
    {
        var tcs = new TaskCompletionSource();
        var clientConfig = CreateClientConfig(type, true);
        var serverConfig = CreateServerConfig(type, true);
        var (server, serverHub, client, clientHub) = CreateServerClient(serverConfig, clientConfig);

        clientConfig.ReconnectionPolicy = new DefaultReconnectionPolicy(new[] { TimeSpan.FromMilliseconds(20) }, true);

        clientHub.OnConnectedEvent = async (_, isReconnected) =>
        {
            if (isReconnected)
                tcs.TrySetResult();
        };

        serverConfig.InternalNoLingerOnShutdown = true;
        serverConfig.InternalForceDisableSendingDisconnectSignal = true;

        server.Start();
        await client.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(1));
        server.Stop();

        // Wait for the client to process the disconnect.
        await Task.Delay(50);

        server.Start();

        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(1));
    }


    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    public async Task ReconnectsNotifiesReconnecting(Type type)
    {
        var tcs = new TaskCompletionSource();
        var clientConfig = CreateClientConfig(type, true);
        var serverConfig = CreateServerConfig(type, true);
        var (server, serverHub, client, clientHub) = CreateServerClient(serverConfig, clientConfig);

        clientHub.OnReconnectingEvent = async _ =>
        {
            tcs.TrySetResult();
        };

        serverConfig.InternalNoLingerOnShutdown = true;
        serverConfig.InternalForceDisableSendingDisconnectSignal = true;

        server.Start();
        await client.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(1));
        server.Stop();

        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    public async Task ReconnectsStopsAfterSpecifiedTimes(Type type)
    {
        var tcs = new TaskCompletionSource();
        var clientConfig = CreateClientConfig(type, true);
        var serverConfig = CreateServerConfig(type, true);
        var (server, serverHub, client, clientHub) = CreateServerClient(serverConfig, clientConfig);

        clientConfig.ConnectionTimeout = 100;

        clientConfig.ReconnectionPolicy = new DefaultReconnectionPolicy(new[]
        {
            TimeSpan.FromMilliseconds(20)
        }, false);


        clientHub.OnDisconnectedEvent = async _ =>
        {
            tcs.TrySetResult();
        };

        serverConfig.InternalNoLingerOnShutdown = true;
        serverConfig.InternalForceDisableSendingDisconnectSignal = true;

        server.Start();
        await client.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(1));
        server.Stop();

        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }



}
