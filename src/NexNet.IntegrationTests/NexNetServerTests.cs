﻿using NUnit.Framework;
#pragma warning disable VSTHRD200

namespace NexNet.IntegrationTests;

internal partial class NexNetServerTests : BaseTests
{

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    public async Task AcceptsClientConnection(Type type)
    {
        var tcs = new TaskCompletionSource();
        var serverConfig = CreateServerConfig(type, false);
        var (server, serverHub, client, clientHub) = CreateServerClient(
            serverConfig,
            CreateClientConfig(type, false));

        serverConfig.InternalOnConnect = () => tcs.SetResult();

        server.Start();
        await client.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(1));

        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(1));
    }


    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    public async Task HubFiresOnConnected(Type type)
    {
        var tcs = new TaskCompletionSource();
        var (server, serverHub, client, clientHub) = CreateServerClient(
            CreateServerConfig(type, false),
            CreateClientConfig(type, false));

        serverHub.OnConnectedEvent = hub =>
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
    public async Task StartsAndStopsMultipleTimes(Type type)
    {
        var (server, _, client, clientHub) = CreateServerClient(
            CreateServerConfig(type, false),
            CreateClientConfig(type, false));


        for (int i = 0; i < 3; i++)
        {
            server.Start();
            await client.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(1));

            await client.ReadyTask.WaitAsync(TimeSpan.FromSeconds(1));

            server.Stop();

            await Task.Delay(100);

            // Wait for the client to process the disconnect.

        }
    }

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    public async Task StopsAndReleasesStoppedTcs(Type type)
    {
        var (server, _, client, clientHub) = CreateServerClient(
            CreateServerConfig(type, false),
            CreateClientConfig(type, false));

        
        Assert.IsNull(server.StoppedTcs);
        server.Start();
        Assert.IsFalse(server.StoppedTcs!.IsCompleted);

        server.Stop();

        await server.StoppedTcs!.WaitAsync(TimeSpan.FromSeconds(1));
    }
}
