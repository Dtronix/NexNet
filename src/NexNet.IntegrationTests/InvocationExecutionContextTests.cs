using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NexNet.IntegrationTests.TestInterfaces;
using System.Threading.Tasks;
#pragma warning disable VSTHRD200

namespace NexNet.IntegrationTests;

/// <summary>
/// Invocations run in the execution context of the code that started the session, so methods see its AsyncLocal
/// values (HttpContext, Activity.Current, logging scopes, culture). Each invocation gets its own copy.
/// </summary>
internal class InvocationExecutionContextTests : BaseTests
{
    private static readonly AsyncLocal<string?> Local = new();

    [Test]
    [Transports]
    public async Task ClientInvocation_SeesAsyncLocalOfConnectCaller(Type type)
    {
        var observed = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var (server, client, clientNexus) = CreateServerClient(CreateServerConfig(type), CreateClientConfig(type));
        server.OnNexusCreated = nexus => nexus.OnConnectedEvent = n =>
        {
            n.Context.Clients.Caller.ClientVoid();
            return ValueTask.CompletedTask;
        };
        clientNexus.ClientVoidEvent = _ => observed.TrySetResult(Local.Value);

        await server.StartAsync().Timeout(1);
        Local.Value = "connect";
        await client.ConnectAsync().Timeout(1);

        await Assert.That(await observed.Task.Timeout(1)).IsEqualTo("connect");
    }

    [Test]
    [Transports(Type.Uds, Type.Tcp)]
    public async Task ClientInvocation_KeepsAsyncLocalAcrossAwait(Type type)
    {
        var observed = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var (server, client, clientNexus) = CreateServerClient(CreateServerConfig(type), CreateClientConfig(type));
        server.OnNexusCreated = nexus => nexus.OnConnectedEvent = n =>
        {
            n.Context.Clients.Caller.ClientTask();
            return ValueTask.CompletedTask;
        };
        clientNexus.ClientTaskEvent = async _ =>
        {
            // Completes asynchronously, so the method finishes after its first await.
            await Task.Delay(10);
            observed.TrySetResult(Local.Value);
        };

        await server.StartAsync().Timeout(1);
        Local.Value = "connect";
        await client.ConnectAsync().Timeout(1);

        await Assert.That(await observed.Task.Timeout(1)).IsEqualTo("connect");
    }

    [Test]
    [Transports(Type.Uds, Type.Tcp)]
    public async Task ClientInvocation_AsyncLocalChangeDoesNotReachNextInvocation(Type type)
    {
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var (server, client, clientNexus) = CreateServerClient(CreateServerConfig(type), CreateClientConfig(type));
        clientNexus.ClientVoidWithParamEvent = (_, value) =>
        {
            if (value == 1)
            {
                Local.Value = "changed by first";
                first.TrySetResult();
            }
            else
            {
                second.TrySetResult(Local.Value);
            }
        };

        await server.StartAsync().Timeout(1);
        Local.Value = "connect";
        await client.ConnectAsync().Timeout(1);

        using (var owner = server.Server.ContextProvider.Rent())
            owner.Proxy.All.ClientVoidWithParam(1);
        await first.Task.Timeout(1);

        using (var owner = server.Server.ContextProvider.Rent())
            owner.Proxy.All.ClientVoidWithParam(2);

        await Assert.That(await second.Task.Timeout(1)).IsEqualTo("connect");
    }

    [Test]
    [Transports(Except = new[] { Type.WebSocket, Type.HttpSocket })]
    public async Task ServerInvocation_SeesAsyncLocalOfStartCaller(Type type)
    {
        string? observed = null;
        Local.Value = "start";
        var server = CreateServer(CreateServerConfig(type), nexus => nexus.ServerTaskValueEvent = _ =>
        {
            observed = Local.Value;
            return ValueTask.FromResult(1);
        });
        await server.StartAsync().Timeout(1);
        Local.Value = null;

        var (client, _) = CreateClient(CreateClientConfig(type));
        await client.ConnectAsync().Timeout(1);
        await client.Proxy.ServerTaskValue().Timeout(1);

        await Assert.That(observed).IsEqualTo("start");
    }

    [Test]
    [Transports(Type.WebSocket, Type.HttpSocket)]
    public async Task ServerInvocation_SeesHttpContextOfUpgradeRequest(Type type)
    {
        var path = "";
        var server = CreateServer(
            CreateServerConfig(type),
            nexus => nexus.ServerTaskValueEvent = _ =>
            {
                path = new HttpContextAccessor().HttpContext?.Request.Path.Value ?? "none";
                return ValueTask.FromResult(1);
            },
            onAspCreateServices: builder => builder.Services.AddHttpContextAccessor());
        await server.StartAsync().Timeout(1);

        var (client, _) = CreateClient(CreateClientConfig(type));
        await client.ConnectAsync().Timeout(1);
        await client.Proxy.ServerTaskValue().Timeout(1);

        await Assert.That(path).IsNotEqualTo("none");
        await Assert.That(path).IsNotEmpty();
    }
}