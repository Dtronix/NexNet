﻿using System.Diagnostics;
using System.Net.Sockets;
using MemoryPack;
using NexNet.IntegrationTests.TestInterfaces;
using NexNet.Messages;
using NexNet.Transports;
using NUnit.Framework;
#pragma warning disable VSTHRD200

namespace NexNet.IntegrationTests;

internal partial class NexusClientTests : BaseTests
{
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    public async Task NexusFiresOnConnected(Type type)
    {
        var tcs = new TaskCompletionSource();
        var (server, _, client, clientNexus) = CreateServerClient(
            CreateServerConfig(type),
            CreateClientConfig(type));

        clientNexus.OnConnectedEvent = (_, _) =>
        {
            tcs.SetResult();
            return ValueTask.CompletedTask;
        };

        server.Start();
        await client.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(1));

        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    public async Task ConnectsToServer(Type type)
    {
        var clientConfig = CreateClientConfig(type);
        var tcs = new TaskCompletionSource();
        var (server, _, client, _) = CreateServerClient(
            CreateServerConfig(type),
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
        var clientConfig = CreateClientConfig(type);
        var (_, _, client, _) = CreateServerClient(
            CreateServerConfig(type),
            clientConfig);

        clientConfig.ConnectionTimeout = 100;

        Assert.ThrowsAsync<SocketException>(() => client.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    public void ClientTimesOutWithNoServer(Type type)
    {
        var clientConfig = CreateClientConfig(type);
        var (_, _, client, _) = CreateServerClient(
            CreateServerConfig(type),
            clientConfig);

        clientConfig.ConnectionTimeout = 50;

        Assert.ThrowsAsync<SocketException>(() => client.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    public async Task ConnectsAndDisconnectsMultipleTimesFromServer(Type type)
    {
        var (server, _, client, clientNexus) = CreateServerClient(
            CreateServerConfig(type),
            CreateClientConfig(type));

        server.Start();

        for (int i = 0; i < 5; i++)
        {
            await client.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(1));
            await clientNexus.ConnectedTCS.Task.WaitAsync(TimeSpan.FromSeconds(1));
            clientNexus.ConnectedTCS = new TaskCompletionSource();
            await client.DisconnectAsync().WaitAsync(TimeSpan.FromSeconds(1));
            await clientNexus.DisconnectedTCS.Task.WaitAsync(TimeSpan.FromSeconds(1));
            clientNexus.DisconnectedTCS = new TaskCompletionSource();
        }
    }

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    public void ConnectTimesOutWithNoServer(Type type)
    {
        var (_, _, client, _) = CreateServerClient(
            CreateServerConfig(type),
            CreateClientConfig(type));

        Assert.ThrowsAsync<SocketException>(async () => await client.ConnectAsync());
    }

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    public async Task ClientProvidesAuthenticationToken(Type type)
    {
        var clientConfig = CreateClientConfig(type);
        var tcs = new TaskCompletionSource();
        var (server, _, client, _) = CreateServerClient(
            CreateServerConfig(type),
            clientConfig);

        server.Start();

        clientConfig.Authenticate = () => new byte[] { 123 };
        clientConfig.InternalOnSend= (_, bytes) =>
        {
            var message = MemoryPackSerializer.Deserialize<ClientGreetingMessage>(new ReadOnlySpan<byte>(bytes).Slice(3));

            if (message!.AuthenticationToken![0] == 123)
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
        var clientConfig = CreateClientConfig(type);
        clientConfig.PingInterval = 20;
        var tcs = new TaskCompletionSource();

        var (server, _, client, _) = CreateServerClient(
            CreateServerConfig(type),
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
        var clientConfig = CreateClientConfig(type);
        clientConfig.PingInterval = 20;
        var tcs = new TaskCompletionSource();

        var (server, _, client, clientNexus) = CreateServerClient(
            CreateServerConfig(type),
            clientConfig);

        server.Start();

        await client.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(1));
        await clientNexus.ConnectedTCS.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await client.DisconnectAsync().WaitAsync(TimeSpan.FromSeconds(1));


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
    public async Task ReconnectsOnDisconnect(Type type)
    {
        var tcs = new TaskCompletionSource();
        var clientConfig = CreateClientConfig(type);
        var serverConfig = CreateServerConfig(type);
        var (server, _, client, clientNexus) = CreateServerClient(serverConfig, clientConfig);

        clientConfig.ReconnectionPolicy = new DefaultReconnectionPolicy(new[] { TimeSpan.FromMilliseconds(20) });

        clientNexus.OnConnectedEvent = (_, isReconnected) =>
        {
            if (isReconnected)
                tcs.TrySetResult();
            return ValueTask.CompletedTask;
        };

        serverConfig.InternalNoLingerOnShutdown = true;
        serverConfig.InternalForceDisableSendingDisconnectSignal = true;

        server.Start();
        await client.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(1));
        await clientNexus.ConnectedTCS.Task;
        server.Stop();

        // Wait for the client to process the disconnect.
        await Task.Delay(50);

        server.Start();

        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(1));
    }
    
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    public async Task ReconnectsOnTimeout(Type type)
    {
        var tcs = new TaskCompletionSource();
        var clientConfig = CreateClientConfig(type);
        var serverConfig = CreateServerConfig(type);
        var (server, _, client, clientNexus) = CreateServerClient(serverConfig, clientConfig);

        clientConfig.ReconnectionPolicy = new DefaultReconnectionPolicy(new[] { TimeSpan.FromMilliseconds(20) });

        clientNexus.OnConnectedEvent = (_, isReconnected) =>
        {
            if (isReconnected)
                tcs.TrySetResult();
            return ValueTask.CompletedTask;
        };

        serverConfig.InternalNoLingerOnShutdown = true;
        serverConfig.InternalForceDisableSendingDisconnectSignal = true;
        clientConfig.PingInterval = 75;
        clientConfig.Timeout = 50;

        server.Start();
        await client.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(1));

        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    public async Task ReconnectsNotifiesReconnecting(Type type)
    {
        var tcs = new TaskCompletionSource();
        var clientConfig = CreateClientConfig(type);
        var serverConfig = CreateServerConfig(type);
        var (server, _, client, clientNexus) = CreateServerClient(serverConfig, clientConfig);

        clientNexus.OnReconnectingEvent = _ =>
        {
            tcs.TrySetResult();
            return ValueTask.CompletedTask;
        };

        serverConfig.InternalNoLingerOnShutdown = true;
        serverConfig.InternalForceDisableSendingDisconnectSignal = true;

        server.Start();
        await client.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(1));
        await clientNexus.ConnectedTCS.Task;
        server.Stop();

        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    public async Task ReconnectsStopsAfterSpecifiedTimes(Type type)
    {
        var tcs = new TaskCompletionSource();
        var clientConfig = CreateClientConfig(type);
        var serverConfig = CreateServerConfig(type);
        var (server, _, client, clientNexus) = CreateServerClient(serverConfig, clientConfig);

        clientConfig.ConnectionTimeout = 100;

        clientConfig.ReconnectionPolicy = new DefaultReconnectionPolicy(new[]
        {
            TimeSpan.FromMilliseconds(20)
        }, false);


        clientNexus.OnDisconnectedEvent = _ =>
        {
            tcs.TrySetResult();
            return ValueTask.CompletedTask;
        };

        serverConfig.InternalNoLingerOnShutdown = true;
        serverConfig.InternalForceDisableSendingDisconnectSignal = true;

        server.Start();
        await client.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(1));

        await clientNexus.ConnectedTCS.Task;
        await Task.Delay(100);
        server.Stop();

        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    public async Task ClientProxyInvocationCancelsOnDisconnect(Type type)
    {
        var tcs = new TaskCompletionSource();

        var (server, serverNexus, client, clientNexus) = CreateServerClient(
            CreateServerConfig(type),
            CreateClientConfig(type));

        serverNexus.ServerTaskValueEvent = async _ =>
        {
            await Task.Delay(100000);
            return 12345;
        };

        clientNexus.OnConnectedEvent = async (_, _) =>
        {
            try
            {
                await client.Proxy.ServerTaskValue();
            }
            catch (TaskCanceledException)
            {
                tcs.TrySetResult();
            }
            catch (Exception)
            {
                // ignored
            }
        };

        server.Start();
        await client.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(1));

        await Task.Delay(100);
        server.Stop();
        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(1));
    }


}