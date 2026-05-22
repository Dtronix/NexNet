using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NexNet.Testing.Transports.InProcess;
using NexNet.Transports;
using NUnit.Framework;

namespace NexNet.Testing.Tests;

/// <summary>
/// Isolation tests for <see cref="InProcessTransport"/> and its rendezvous/listener pair.
/// Validates basic round-trip, ordering, and close semantics independently of the NexNet
/// protocol layer.
/// </summary>
internal class InProcessTransportTests
{
    [Test]
    public async Task ServerAndClientTransports_ExchangeDataBidirectionally()
    {
        var endpoint = NewEndpoint();
        var serverConfig = new InProcessServerConfig { Endpoint = endpoint };
        var listener = await ((ServerConfigTestAccess)new ServerConfigTestAccess(serverConfig))
            .CreateListenerAsync();

        var clientConfig = new InProcessClientConfig { Endpoint = endpoint };
        var clientTransport = await ((ClientConfigTestAccess)new ClientConfigTestAccess(clientConfig))
            .ConnectAsync();

        var serverTransport = await listener.AcceptTransportAsync(CancellationToken.None);
        Assert.That(serverTransport, Is.Not.Null);

        // Client -> Server
        await clientTransport.Output.WriteAsync(Encoding.UTF8.GetBytes("hello-from-client"));
        var fromClient = await ReadStringAsync(serverTransport!.Input, "hello-from-client".Length);
        Assert.That(fromClient, Is.EqualTo("hello-from-client"));

        // Server -> Client
        await serverTransport.Output.WriteAsync(Encoding.UTF8.GetBytes("hello-from-server"));
        var fromServer = await ReadStringAsync(clientTransport.Input, "hello-from-server".Length);
        Assert.That(fromServer, Is.EqualTo("hello-from-server"));

        await clientTransport.CloseAsync(true);
        await serverTransport.CloseAsync(true);
        await listener.CloseAsync(true);
    }

    [Test]
    public async Task SequentialMessages_PreserveOrdering()
    {
        var endpoint = NewEndpoint();
        var serverConfig = new InProcessServerConfig { Endpoint = endpoint };
        var listener = await ((ServerConfigTestAccess)new ServerConfigTestAccess(serverConfig))
            .CreateListenerAsync();

        var clientConfig = new InProcessClientConfig { Endpoint = endpoint };
        var clientTransport = await ((ClientConfigTestAccess)new ClientConfigTestAccess(clientConfig))
            .ConnectAsync();

        var serverTransport = await listener.AcceptTransportAsync(CancellationToken.None);
        Assert.That(serverTransport, Is.Not.Null);

        for (int i = 0; i < 50; i++)
            await clientTransport.Output.WriteAsync(new byte[] { (byte)i });

        var bytes = await ReadBytesAsync(serverTransport!.Input, 50);
        for (int i = 0; i < 50; i++)
            Assert.That(bytes[i], Is.EqualTo((byte)i), $"position {i}");

        await clientTransport.CloseAsync(true);
        await serverTransport.CloseAsync(true);
        await listener.CloseAsync(true);
    }

    [Test]
    public async Task ClientClose_CompletesPeerReader()
    {
        var endpoint = NewEndpoint();
        var serverConfig = new InProcessServerConfig { Endpoint = endpoint };
        var listener = await ((ServerConfigTestAccess)new ServerConfigTestAccess(serverConfig))
            .CreateListenerAsync();

        var clientConfig = new InProcessClientConfig { Endpoint = endpoint };
        var clientTransport = await ((ClientConfigTestAccess)new ClientConfigTestAccess(clientConfig))
            .ConnectAsync();

        var serverTransport = await listener.AcceptTransportAsync(CancellationToken.None);
        Assert.That(serverTransport, Is.Not.Null);

        await clientTransport.CloseAsync(true);

        var read = await serverTransport!.Input.ReadAsync();
        Assert.That(read.IsCompleted, Is.True, "Peer reader must observe completion after close");

        await serverTransport.CloseAsync(true);
        await listener.CloseAsync(true);
    }

    [Test]
    public void ConnectWithoutListener_Throws()
    {
        var clientConfig = new InProcessClientConfig { Endpoint = NewEndpoint() };
        Assert.ThrowsAsync<TransportException>(async () =>
            await ((ClientConfigTestAccess)new ClientConfigTestAccess(clientConfig)).ConnectAsync());
    }

    [Test]
    public async Task DuplicateEndpoint_SecondListenerRegistrationThrows()
    {
        var endpoint = NewEndpoint();
        var first = new InProcessServerConfig { Endpoint = endpoint };
        var firstListener = await ((ServerConfigTestAccess)new ServerConfigTestAccess(first))
            .CreateListenerAsync();

        var second = new InProcessServerConfig { Endpoint = endpoint };
        Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await ((ServerConfigTestAccess)new ServerConfigTestAccess(second)).CreateListenerAsync());

        await firstListener.CloseAsync(true);
    }

    private static string NewEndpoint() => $"test-{Guid.NewGuid():N}";

    private static async Task<string> ReadStringAsync(PipeReader reader, int byteCount)
    {
        var bytes = await ReadBytesAsync(reader, byteCount);
        return Encoding.UTF8.GetString(bytes);
    }

    private static async Task<byte[]> ReadBytesAsync(PipeReader reader, int byteCount)
    {
        var collected = new byte[byteCount];
        var collectedSpan = collected.AsMemory();
        var offset = 0;
        while (offset < byteCount)
        {
            var read = await reader.ReadAsync();
            var buffer = read.Buffer;
            var toCopy = (int)Math.Min(buffer.Length, byteCount - offset);
            buffer.Slice(0, toCopy).CopyTo(collectedSpan.Slice(offset).Span);
            offset += toCopy;
            reader.AdvanceTo(buffer.GetPosition(toCopy));
            if (read.IsCompleted && offset < byteCount)
                throw new InvalidOperationException($"Stream ended after {offset}/{byteCount} bytes");
        }
        return collected;
    }

    /// <summary>Test shim that invokes the protected <c>OnCreateServerListener</c> via reflection.</summary>
    private sealed class ServerConfigTestAccess
    {
        private readonly InProcessServerConfig _config;
        public ServerConfigTestAccess(InProcessServerConfig config) => _config = config;

        public async Task<ITransportListener> CreateListenerAsync()
        {
            var method = typeof(ServerConfig).GetMethod("OnCreateServerListener",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            ValueTask<ITransportListener?> task;
            try
            {
                task = (ValueTask<ITransportListener?>)method.Invoke(_config, new object[] { CancellationToken.None })!;
            }
            catch (System.Reflection.TargetInvocationException tie) when (tie.InnerException is not null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(tie.InnerException).Throw();
                throw; // unreachable
            }
            var listener = await task;
            return listener!;
        }
    }

    private sealed class ClientConfigTestAccess
    {
        private readonly InProcessClientConfig _config;
        public ClientConfigTestAccess(InProcessClientConfig config) => _config = config;

        public async Task<ITransport> ConnectAsync()
        {
            var method = typeof(ClientConfig).GetMethod("OnConnectTransport",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            ValueTask<ITransport> task;
            try
            {
                task = (ValueTask<ITransport>)method.Invoke(_config, new object[] { CancellationToken.None })!;
            }
            catch (System.Reflection.TargetInvocationException tie) when (tie.InnerException is not null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(tie.InnerException).Throw();
                throw; // unreachable
            }
            return await task;
        }
    }
}
