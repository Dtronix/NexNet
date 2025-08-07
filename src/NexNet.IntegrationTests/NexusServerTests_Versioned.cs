using NexNet.IntegrationTests.TestInterfaces;
using NUnit.Framework;
#pragma warning disable VSTHRD200

namespace NexNet.IntegrationTests;

internal partial class NexusServerTests_Versioned : BaseTests
{

    [TestCase(Type.Quic)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task CurrentServerProxy_ConnectsToServerSuccessfully(Type type)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var serverConfig = CreateServerConfig(type);
        var server = CreateServer<VersionedServerNexusV2, VersionedServerNexusV2.ClientProxy>(serverConfig, null);
        var clientConfig = CreateClientConfig(type);
        var client = CreateClient<VersionedClientNexusV2, VersionedClientNexusV2.ServerProxy>(clientConfig);
        
        await server.StartAsync();
        serverConfig.InternalOnConnect = () => tcs.SetResult();

        await client.client.ConnectAsync().Timeout(1);
        
        await tcs.Task.Timeout(1);
    }
    
    [TestCase(Type.Quic)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task CurrentServerProxy_InvokesCurrentAndOlderMethodVersions(Type type)
    {
        var tcsRunTaskNewV2Action = new TaskCompletionSource();
        var tcsRunTaskV1_1Action = new TaskCompletionSource();
        var tcsRunTaskWithResultV1_1Action = new TaskCompletionSource();
        var tcsVerifyVersionV1Action = new TaskCompletionSource();
        
        var serverConfig = CreateServerConfig(type);
        var server = CreateServer<VersionedServerNexusV2, VersionedServerNexusV2.ClientProxy>(serverConfig, n =>
        {
            n.RunTaskNewV2Action = () =>
            {
                tcsRunTaskNewV2Action.TrySetResult();
                return ValueTask.CompletedTask;
            };
            n.RunTaskV1_1Action = () => tcsRunTaskV1_1Action.TrySetResult();
            n.RunTaskWithResultV1_1Action = () =>
            {
                tcsRunTaskWithResultV1_1Action.TrySetResult();
                return default;
            };
            n.VerifyVersionV1Action = s =>
            {
                tcsVerifyVersionV1Action.TrySetResult();
                return new ValueTask<bool>(true);
            };

        });
        var clientConfig = CreateClientConfig(type);
        var client = CreateClient<VersionedClientNexusV2, VersionedClientNexusV2.ServerProxy>(clientConfig);
        
        await server.StartAsync();
        await client.client.ConnectAsync().Timeout(1);

        await client.client.Proxy.RunTaskNewV2();
        client.client.Proxy.RunTaskV1_1();
        await client.client.Proxy.RunTaskWithResultV1_1();
        await client.client.Proxy.VerifyVersionV1("");

        await tcsRunTaskNewV2Action.Task.Timeout(1);
        await tcsRunTaskV1_1Action.Task.Timeout(1);
        await tcsRunTaskWithResultV1_1Action.Task.Timeout(1);
        await tcsVerifyVersionV1Action.Task.Timeout(1);
    }
    
    [TestCase(Type.Quic)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task LowerServerProxy_ConnectsToServerSuccessfully(Type type)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var serverConfig = CreateServerConfig(type);
        var server = CreateServer<VersionedServerNexusV2, VersionedServerNexusV2.ClientProxy>(serverConfig, null);
        var clientConfig = CreateClientConfig(type);
        var client = CreateClient<VersionedClientNexusV1_1, VersionedClientNexusV1_1.ServerProxy>(clientConfig);
        
        await server.StartAsync();
        serverConfig.InternalOnConnect = () => tcs.SetResult();

        await client.client.ConnectAsync().Timeout(1);
        
        await tcs.Task.Timeout(1);
    }
    
        [TestCase(Type.Quic)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task LowerServerProxy_InvokesCurrentAndOlderMethodVersions(Type type)
    {
        var tcsRunTaskV1_1Action = new TaskCompletionSource();
        var tcsRunTaskWithResultV1_1Action = new TaskCompletionSource();
        var tcsVerifyVersionV1Action = new TaskCompletionSource();
        
        var serverConfig = CreateServerConfig(type);
        var server = CreateServer<VersionedServerNexusV2, VersionedServerNexusV2.ClientProxy>(serverConfig, n =>
        {
            n.RunTaskV1_1Action = () => tcsRunTaskV1_1Action.TrySetResult();
            n.RunTaskWithResultV1_1Action = () =>
            {
                tcsRunTaskWithResultV1_1Action.TrySetResult();
                return default;
            };
            n.VerifyVersionV1Action = s =>
            {
                tcsVerifyVersionV1Action.TrySetResult();
                return new ValueTask<bool>(true);
            };

        });
        var clientConfig = CreateClientConfig(type);
        var client = CreateClient<VersionedClientNexusV1_1, VersionedClientNexusV1_1.ServerProxy>(clientConfig);
        
        await server.StartAsync();
        await client.client.ConnectAsync().Timeout(1);
        
        client.client.Proxy.RunTaskV1_1();
        await client.client.Proxy.RunTaskWithResultV1_1();
        await client.client.Proxy.VerifyVersionV1("");
        
        await tcsRunTaskV1_1Action.Task.Timeout(1);
        await tcsRunTaskWithResultV1_1Action.Task.Timeout(1);
        await tcsVerifyVersionV1Action.Task.Timeout(1);
    }
    
    [TestCase(Type.Quic)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task LowerServerProxy_ConnectsToServerSuccessfully2(Type type)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var serverConfig = CreateServerConfig(type);
        var server = CreateServer<VersionedServerNexusV2, VersionedServerNexusV2.ClientProxy>(serverConfig, null);
        var clientConfig = CreateClientConfig(type);
        var client = CreateClient<VersionedClientNexusV1, VersionedClientNexusV1.ServerProxy>(clientConfig);
        
        await server.StartAsync();
        serverConfig.InternalOnConnect = () => tcs.SetResult();

        await client.client.ConnectAsync().Timeout(1);
        
        await tcs.Task.Timeout(1);
    }
}

