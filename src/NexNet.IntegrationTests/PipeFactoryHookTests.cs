using NexNet.Internals;
using NexNet.IntegrationTests.TestInterfaces;
using NexNet.Pipes;
using NUnit.Framework;
#pragma warning disable VSTHRD200

namespace NexNet.IntegrationTests;

internal class PipeFactoryHookTests : BaseTests
{
    [TestCase(Type.Tcp)]
    [TestCase(Type.InProcess)]
    public async Task NoFactory_PipeBehavesNormally(Type type)
    {
        // Sanity check: with no factory installed, pipe round-trip still works.
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (server, client, cNexus) = CreateServerClient(
            CreateServerConfig(type),
            CreateClientConfig(type));

        cNexus.ClientTaskValueWithDuplexPipeEvent = async (_, pipe) =>
        {
            var result = await pipe.Input.ReadAsync();
            if (!result.IsCompleted)
                tcs.SetResult();
        };

        await server.StartAsync().Timeout(1);
        await client.ConnectAsync().Timeout(1);

        var sNexus = server.NexusCreatedQueue.First();
        await using var pipe = sNexus.Context.CreatePipe();
        await sNexus.Context.Clients.Caller.ClientTaskValueWithDuplexPipe(pipe).Timeout(1);
        await pipe.ReadyTask.Timeout(1);
        await pipe.Output.WriteAsync(new byte[] { 1, 2, 3 }).Timeout(1);

        await tcs.Task.Timeout(1);
    }

    [TestCase(Type.Tcp)]
    [TestCase(Type.InProcess)]
    public async Task Factory_WrapsLocalAndRemotePipes(Type type)
    {
        var serverFactory = new CountingPipeFactory();
        var clientFactory = new CountingPipeFactory();

        var serverConfig = CreateServerConfig(type);
        var clientConfig = CreateClientConfig(type);
        serverConfig.PipeFactory = serverFactory;
        clientConfig.PipeFactory = clientFactory;

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (server, client, cNexus) = CreateServerClient(serverConfig, clientConfig);

        cNexus.ClientTaskValueWithDuplexPipeEvent = async (_, pipe) =>
        {
            var result = await pipe.Input.ReadAsync();
            if (!result.IsCompleted)
                tcs.SetResult();
        };

        await server.StartAsync().Timeout(1);
        await client.ConnectAsync().Timeout(1);

        var sNexus = server.NexusCreatedQueue.First();
        await using var pipe = sNexus.Context.CreatePipe();
        await sNexus.Context.Clients.Caller.ClientTaskValueWithDuplexPipe(pipe).Timeout(1);
        await pipe.ReadyTask.Timeout(1);
        await pipe.Output.WriteAsync(new byte[] { 1, 2, 3 }).Timeout(1);

        await tcs.Task.Timeout(1);

        // The server side rented the pipe (WrapLocal); the client side registered the
        // remote pipe in response (WrapRemote).
        Assert.That(serverFactory.LocalWrapped, Is.GreaterThanOrEqualTo(1), "Server WrapLocal");
        Assert.That(clientFactory.RemoteWrapped, Is.GreaterThanOrEqualTo(1), "Client WrapRemote");
    }

    private sealed class CountingPipeFactory : IPipeFactory
    {
        public int LocalWrapped;
        public int RemoteWrapped;

        public IRentedNexusDuplexPipe WrapLocal(IRentedNexusDuplexPipe inner)
        {
            Interlocked.Increment(ref LocalWrapped);
            return inner;
        }

        public INexusDuplexPipe WrapRemote(INexusDuplexPipe inner)
        {
            Interlocked.Increment(ref RemoteWrapped);
            return inner;
        }
    }
}
