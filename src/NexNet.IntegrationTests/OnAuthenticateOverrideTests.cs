using NexNet.IntegrationTests.TestInterfaces;
using NexNet.Messages;
using NUnit.Framework;
#pragma warning disable VSTHRD200

namespace NexNet.IntegrationTests;

internal class OnAuthenticateOverrideTests : BaseTests
{
    [TestCase(Type.Tcp)]
    public async Task NoOverride_OnAuthenticateIsCalled(Type type)
    {
        // Baseline: when no override is installed, the nexus's OnAuthenticate is the auth source.
        var serverConfig = CreateServerConfig(type);
        serverConfig.Authenticate = true;

        var onAuthenticateCalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (server, client, _) = CreateServerClient(serverConfig, CreateClientConfig(type));

        server.OnNexusCreated = nexus =>
        {
            nexus.OnAuthenticateEvent = _ =>
            {
                onAuthenticateCalled.TrySetResult();
                return ValueTask.FromResult<IIdentity?>(new DefaultIdentity());
            };
        };

        await server.StartAsync().Timeout(1);
        await client.ConnectAsync().Timeout(1);

        await onAuthenticateCalled.Task.Timeout(1);
    }

    [TestCase(Type.Tcp)]
    public async Task Override_ConsultedInsteadOfOnAuthenticate(Type type)
    {
        var serverConfig = CreateServerConfig(type);
        serverConfig.Authenticate = true;

        var overrideCalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var onAuthenticateCalled = false;

        serverConfig.OnAuthenticateOverride = _ =>
        {
            overrideCalled.TrySetResult();
            return ValueTask.FromResult<IIdentity?>(new DefaultIdentity());
        };

        var (server, client, _) = CreateServerClient(serverConfig, CreateClientConfig(type));

        server.OnNexusCreated = nexus =>
        {
            nexus.OnAuthenticateEvent = _ =>
            {
                onAuthenticateCalled = true;
                return ValueTask.FromResult<IIdentity?>(new DefaultIdentity());
            };
        };

        await server.StartAsync().Timeout(1);
        await client.ConnectAsync().Timeout(1);

        await overrideCalled.Task.Timeout(1);
        Assert.That(onAuthenticateCalled, Is.False, "OnAuthenticate must not be called when override is installed");
    }

    [TestCase(Type.Tcp)]
    public async Task Override_NullIdentity_DisconnectsClient(Type type)
    {
        // The override returning null mirrors OnAuthenticate-returns-null behavior: server sends auth-disconnect.
        var disconnectSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var serverConfig = CreateServerConfig(type);
        serverConfig.Authenticate = true;
        serverConfig.OnAuthenticateOverride = _ => ValueTask.FromResult<IIdentity?>(null);
        serverConfig.InternalOnSend = (_, bytes) =>
        {
            if (bytes is [(byte)MessageType.DisconnectAuthentication])
                disconnectSent.TrySetResult();
        };

        var (server, client, _) = CreateServerClient(serverConfig, CreateClientConfig(type));

        await server.StartAsync().Timeout(1);
        await client.TryConnectAsync().Timeout(1);

        await disconnectSent.Task.Timeout(1);
    }
}
