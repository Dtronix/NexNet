using System.Buffers;
using NexNet.IntegrationTests.TestInterfaces;
using NexNet.Internals;
using NexNet.Invocation;
using NexNet.Messages;
using NexNet.Serialization;
using NexNet.Transports;
using NUnit.Framework;

#pragma warning disable VSTHRD200

namespace NexNet.IntegrationTests;

/// <summary>
/// Remote exception detail: the opt-in type and message, the application error hook, and the optional third element
/// of an Exception invocation result.
/// </summary>
internal class RemoteExceptionDetailTests : BaseTests
{
    private const string DefaultMessage = "Exception occurred while executing method on proxy.";

    // ----- Caller side, from the message bodies directly -----

    private delegate void DetailWriter(ref MsgPackWriter writer);

    private static byte[] ExceptionResultBody(DetailWriter? detail)
    {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new MsgPackWriter(buffer);
        writer.WriteArrayHeader(detail == null ? 2 : 3);
        writer.Write((ushort)1);
        writer.Write((byte)InvocationResultMessage.StateType.Exception);
        detail?.Invoke(ref writer);
        writer.Flush();
        return buffer.WrittenSpan.ToArray();
    }

    private static async Task<ProxyRemoteInvocationException> Throws(Func<Task> call)
        => (await Assert.ThrowsAsync<ProxyRemoteInvocationException>(call))!;

    private static Task<ProxyRemoteInvocationException> ThrowFromBody(byte[] body)
    {
        var message = new InvocationResultMessage();
        var reader = new MsgPackReader(new ReadOnlyMemory<byte>(body), NexusSerializerOptions.Untrusted);
        message.Deserialize(ref reader);

        var state = RegisteredInvocationState<int>.Rent();
        state.Result = message;
        Assert.That(state.TrySetResult(), Is.True);

        var valueTask = new ValueTask<int>(state, state.Version);
        return Throws(async () => await valueTask);
    }

    [Test]
    public async Task OldTwoElementExceptionResult_ThrowsPlainException()
    {
        var exception = await ThrowFromBody(ExceptionResultBody(null));

        Assert.That(exception.GetType(), Is.EqualTo(typeof(ProxyRemoteInvocationException)));
        Assert.That(exception.Message, Is.EqualTo(DefaultMessage));
        Assert.That(exception.RemoteErrorCode, Is.Null);
        Assert.That(exception.RemoteExceptionType, Is.Null);
        Assert.That(exception.RemoteMessage, Is.Null);
    }

    [Test]
    public async Task ExceptionResultWithDetail_ThrowsWithDetail()
    {
        var exception = await ThrowFromBody(ExceptionResultBody((ref MsgPackWriter w) =>
        {
            w.WriteArrayHeader(3);
            w.Write(42);
            w.Write("System.InvalidOperationException");
            w.Write("boom");
        }));

        Assert.That(exception.RemoteErrorCode, Is.EqualTo(42));
        Assert.That(exception.RemoteExceptionType, Is.EqualTo("System.InvalidOperationException"));
        Assert.That(exception.RemoteMessage, Is.EqualTo("boom"));
        Assert.That(exception.Message, Does.StartWith(DefaultMessage));
        Assert.That(exception.Message, Does.Contain("boom"));
    }

    [Test]
    public async Task ExceptionResultWithNilDetailFields_ThrowsWithoutThem()
    {
        var exception = await ThrowFromBody(ExceptionResultBody((ref MsgPackWriter w) =>
        {
            w.WriteArrayHeader(3);
            w.Write(7);
            w.WriteNil();
            w.WriteNil();
        }));

        Assert.That(exception.RemoteErrorCode, Is.EqualTo(7));
        Assert.That(exception.RemoteExceptionType, Is.Null);
        Assert.That(exception.RemoteMessage, Is.Null);
        Assert.That(exception.Message, Does.StartWith(DefaultMessage));
    }

    // Well-formed MessagePack values that are not a valid detail.
    [TestCase("int instead of array")]
    [TestCase("two-element array")]
    [TestCase("four-element array")]
    [TestCase("string code")]
    [TestCase("int type")]
    [TestCase("array message")]
    [TestCase("nil detail")]
    public async Task ExceptionResultWithMalformedDetail_ThrowsPlainException(string shape)
    {
        DetailWriter detail = shape switch
        {
            "int instead of array" => (ref MsgPackWriter w) => w.Write(5),
            "two-element array" => (ref MsgPackWriter w) => { w.WriteArrayHeader(2); w.Write(1); w.Write("t"); },
            "four-element array" => (ref MsgPackWriter w) =>
            {
                w.WriteArrayHeader(4); w.Write(1); w.Write("t"); w.Write("m"); w.Write("x");
            },
            "string code" => (ref MsgPackWriter w) => { w.WriteArrayHeader(3); w.Write("1"); w.Write("t"); w.Write("m"); },
            "int type" => (ref MsgPackWriter w) => { w.WriteArrayHeader(3); w.Write(1); w.Write(2); w.Write("m"); },
            "array message" => (ref MsgPackWriter w) =>
            {
                w.WriteArrayHeader(3); w.Write(1); w.Write("t"); w.WriteArrayHeader(1); w.Write("m");
            },
            "nil detail" => (ref MsgPackWriter w) => w.WriteNil(),
            _ => throw new ArgumentOutOfRangeException(nameof(shape))
        };

        var exception = await ThrowFromBody(ExceptionResultBody(detail));

        Assert.That(exception.GetType(), Is.EqualTo(typeof(ProxyRemoteInvocationException)));
        Assert.That(exception.Message, Is.EqualTo(DefaultMessage));
        Assert.That(exception.RemoteErrorCode, Is.Null);
        Assert.That(exception.RemoteExceptionType, Is.Null);
        Assert.That(exception.RemoteMessage, Is.Null);
    }

    [TestCase(257, 10)]
    [TestCase(10, 2049)]
    public async Task ExceptionResultWithOversizedDetail_ThrowsPlainException(int typeLength, int messageLength)
    {
        var exception = await ThrowFromBody(ExceptionResultBody((ref MsgPackWriter w) =>
        {
            w.WriteArrayHeader(3);
            w.Write(1);
            w.Write(new string('t', typeLength));
            w.Write(new string('m', messageLength));
        }));

        Assert.That(exception.Message, Is.EqualTo(DefaultMessage));
        Assert.That(exception.RemoteErrorCode, Is.Null);
        Assert.That(exception.RemoteExceptionType, Is.Null);
        Assert.That(exception.RemoteMessage, Is.Null);
    }

    [Test]
    public void ExceptionConstructedWithDetail_IncludesRemoteMessage()
    {
        var exception = new ProxyRemoteInvocationException(42, "System.InvalidOperationException", "boom");

        Assert.That(exception.RemoteErrorCode, Is.EqualTo(42));
        Assert.That(exception.RemoteExceptionType, Is.EqualTo("System.InvalidOperationException"));
        Assert.That(exception.RemoteMessage, Is.EqualTo("boom"));
        Assert.That(exception.Message, Does.StartWith(DefaultMessage));
        Assert.That(exception.Message, Does.Contain("boom"));

        var plain = new ProxyRemoteInvocationException();
        Assert.That(plain.Message, Is.EqualTo(DefaultMessage));
        Assert.That(plain.RemoteErrorCode, Is.Null);
        Assert.That(plain.RemoteExceptionType, Is.Null);
        Assert.That(plain.RemoteMessage, Is.Null);
    }

    // ----- End to end -----

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    public async Task NoneByDefault_ExceptionResultBytesAreUnchanged(Type type)
    {
        var serverConfig = CreateServerConfig(type);
        Assert.That(serverConfig.RemoteExceptionDetail, Is.EqualTo(RemoteExceptionDetail.None));
        Assert.That(CreateClientConfig(type).RemoteExceptionDetail, Is.EqualTo(RemoteExceptionDetail.None));

        var results = new List<byte[]>();
        serverConfig.InternalOnSend = (_, bytes) =>
        {
            if (bytes.Length > 0 && bytes[0] == (byte)MessageType.InvocationResult)
                lock (results)
                    results.Add(bytes);
        };

        var (server, client, _) = CreateServerClient(serverConfig, CreateClientConfig(type));
        server.OnNexusCreated = nexus => nexus.ServerTaskValueEvent = _ => throw new InvalidOperationException("secret");

        await server.StartAsync().Timeout(1);
        await client.ConnectAsync().Timeout(1);

        var exception = await Throws(
            async () => await client.Proxy.ServerTaskValue().Timeout(1));
        Assert.That(exception.Message, Is.EqualTo(DefaultMessage));
        Assert.That(exception.RemoteErrorCode, Is.Null);
        Assert.That(exception.RemoteExceptionType, Is.Null);
        Assert.That(exception.RemoteMessage, Is.Null);

        byte[] frame;
        lock (results)
        {
            Assert.That(results, Has.Count.EqualTo(1));
            frame = results[0];
        }

        // Header: type (1), body length (u16 LE); body: fixarray(2), invocation id (positive fixint), state 2.
        var body = frame.AsSpan(3).ToArray();
        Assert.That(BitConverter.ToUInt16(frame, 1), Is.EqualTo(body.Length));
        Assert.That(body, Has.Length.EqualTo(3));
        Assert.That(body[0], Is.EqualTo(0x92));
        Assert.That(body[2], Is.EqualTo((byte)InvocationResultMessage.StateType.Exception));
    }

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    public async Task TypeAndMessage_ClientCallingServer_CarriesTypeAndMessage(Type type)
    {
        var serverConfig = CreateServerConfig(type);
        serverConfig.RemoteExceptionDetail = RemoteExceptionDetail.TypeAndMessage;
        var (server, client, _) = CreateServerClient(serverConfig, CreateClientConfig(type));
        server.OnNexusCreated = nexus => nexus.ServerTaskValueEvent = _ => throw new InvalidOperationException("boom");

        await server.StartAsync().Timeout(1);
        await client.ConnectAsync().Timeout(1);

        var exception = await Throws(
            async () => await client.Proxy.ServerTaskValue().Timeout(1));
        Assert.That(exception.RemoteErrorCode, Is.Null);
        Assert.That(exception.RemoteExceptionType, Is.EqualTo("System.InvalidOperationException"));
        Assert.That(exception.RemoteMessage, Is.EqualTo("boom"));
        Assert.That(exception.Message, Does.Contain("boom"));

        // The session stays usable.
        server.NexusCreatedQueue.TryPeek(out var serverNexus);
        serverNexus!.ServerTaskValueEvent = _ => ValueTask.FromResult(5);
        Assert.That(await client.Proxy.ServerTaskValue().Timeout(1), Is.EqualTo(5));
    }

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    public async Task TypeAndMessage_TruncatesLongMessage(Type type)
    {
        var serverConfig = CreateServerConfig(type);
        serverConfig.RemoteExceptionDetail = RemoteExceptionDetail.TypeAndMessage;
        var (server, client, _) = CreateServerClient(serverConfig, CreateClientConfig(type));
        var longMessage = new string('x', 5000);
        server.OnNexusCreated = nexus => nexus.ServerTaskValueEvent = _ => throw new InvalidOperationException(longMessage);

        await server.StartAsync().Timeout(1);
        await client.ConnectAsync().Timeout(1);

        var exception = await Throws(
            async () => await client.Proxy.ServerTaskValue().Timeout(1));
        Assert.That(exception.RemoteExceptionType, Is.EqualTo("System.InvalidOperationException"));
        Assert.That(exception.RemoteMessage, Is.EqualTo(new string('x', 2048)));
    }

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    public async Task TypeAndMessage_LimitFailureCarriesItsType(Type type)
    {
        var serverConfig = CreateServerConfig(type);
        serverConfig.RemoteExceptionDetail = RemoteExceptionDetail.TypeAndMessage;
        var (server, client, clientNexus) = CreateServerClient(serverConfig, CreateClientConfig(type));

        await server.StartAsync().Timeout(1);
        await client.ConnectAsync().Timeout(1);

        // Larger than the client accepts (4 MiB by default): the server fails the call with a limit exception.
        var exception = await Throws(
            async () => await clientNexus.Context.Proxy.ServerDataResult(5 * 1024 * 1024).Timeout(5));
        Assert.That(exception.RemoteExceptionType, Is.EqualTo(typeof(NexusMessageTooLargeException).FullName));
        Assert.That(exception.RemoteMessage, Is.Not.Null.And.Not.Empty);
    }

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    public async Task Hook_ResultIsSentWhateverTheSetting(Type type)
    {
        Exception? seen = null;
        var thrown = new InvalidOperationException("secret");
        var (server, client, _) = CreateServerClient(CreateServerConfig(type), CreateClientConfig(type));
        server.OnNexusCreated = nexus =>
        {
            nexus.ServerTaskValueEvent = _ => throw thrown;
            nexus.OnInvocationExceptionEvent = (_, exception, _) =>
            {
                seen = exception;
                return new NexusRemoteError(42, "denied");
            };
        };

        await server.StartAsync().Timeout(1);
        await client.ConnectAsync().Timeout(1);

        var exception = await Throws(
            async () => await client.Proxy.ServerTaskValue().Timeout(1));
        Assert.That(seen, Is.SameAs(thrown));
        Assert.That(exception.RemoteErrorCode, Is.EqualTo(42));
        Assert.That(exception.RemoteExceptionType, Is.Null);
        Assert.That(exception.RemoteMessage, Is.EqualTo("denied"));
        Assert.That(exception.Message, Does.Contain("denied"));
        Assert.That(exception.Message, Does.Not.Contain("secret"));
    }

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    public async Task Hook_ReturningNull_FallsBackToTheSetting(Type type)
    {
        var serverConfig = CreateServerConfig(type);
        serverConfig.RemoteExceptionDetail = RemoteExceptionDetail.TypeAndMessage;
        var (server, client, _) = CreateServerClient(serverConfig, CreateClientConfig(type));
        server.OnNexusCreated = nexus =>
        {
            nexus.ServerTaskValueEvent = _ => throw new ArgumentException("bad");
            nexus.OnInvocationExceptionEvent = (_, _, _) => null;
        };

        await server.StartAsync().Timeout(1);
        await client.ConnectAsync().Timeout(1);

        var exception = await Throws(
            async () => await client.Proxy.ServerTaskValue().Timeout(1));
        Assert.That(exception.RemoteErrorCode, Is.Null);
        Assert.That(exception.RemoteExceptionType, Is.EqualTo("System.ArgumentException"));
        Assert.That(exception.RemoteMessage, Is.EqualTo("bad"));
    }

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    public async Task Hook_Throwing_FallsBackToTheSettingAndSessionSurvives(Type type)
    {
        var serverConfig = CreateServerConfig(type);
        serverConfig.RemoteExceptionDetail = RemoteExceptionDetail.TypeAndMessage;
        var (server, client, _) = CreateServerClient(serverConfig, CreateClientConfig(type));
        var fail = true;
        server.OnNexusCreated = nexus =>
        {
            nexus.ServerTaskValueEvent = _ => fail ? throw new InvalidOperationException("boom") : ValueTask.FromResult(9);
            nexus.OnInvocationExceptionEvent = (_, _, _) => throw new NotSupportedException("hook failed");
        };

        await server.StartAsync().Timeout(1);
        await client.ConnectAsync().Timeout(1);

        var exception = await Throws(
            async () => await client.Proxy.ServerTaskValue().Timeout(1));
        Assert.That(exception.RemoteErrorCode, Is.Null);
        Assert.That(exception.RemoteExceptionType, Is.EqualTo("System.InvalidOperationException"));
        Assert.That(exception.RemoteMessage, Is.EqualTo("boom"));

        fail = false;
        Assert.That(await client.Proxy.ServerTaskValue().Timeout(1), Is.EqualTo(9));
        Assert.That(client.State, Is.EqualTo(ConnectionState.Connected));
    }

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    public async Task TypeAndMessage_ServerCallingClient_CarriesTypeAndMessage(Type type)
    {
        var clientConfig = CreateClientConfig(type);
        clientConfig.RemoteExceptionDetail = RemoteExceptionDetail.TypeAndMessage;
        var (server, client, clientNexus) = CreateServerClient(CreateServerConfig(type), clientConfig);
        clientNexus.ClientTaskValueEvent = _ => throw new InvalidOperationException("client boom");

        await server.StartAsync().Timeout(1);
        await client.ConnectAsync().Timeout(1);

        using var context = server.Server.ContextProvider.Rent();
        var exception = await Throws(
            async () => await context.Proxy.Client(context.Proxy.GetIds().First()).ClientTaskValue().Timeout(1));
        Assert.That(exception.RemoteExceptionType, Is.EqualTo("System.InvalidOperationException"));
        Assert.That(exception.RemoteMessage, Is.EqualTo("client boom"));
    }

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    public async Task Hook_ServerCallingClient_CarriesApplicationError(Type type)
    {
        var (server, client, clientNexus) = CreateServerClient(CreateServerConfig(type), CreateClientConfig(type));
        clientNexus.ClientTaskValueEvent = _ => throw new InvalidOperationException("client secret");
        clientNexus.OnInvocationExceptionEvent = (_, _, _) => new NexusRemoteError(7, null);

        var tcs = new TaskCompletionSource<ProxyRemoteInvocationException>(TaskCreationOptions.RunContinuationsAsynchronously);
        server.OnNexusCreated = nexus => nexus.OnConnectedEvent = async n =>
        {
            try
            {
                await n.Context.Clients.Caller.ClientTaskValue();
                tcs.TrySetException(new AssertionException("The call did not fail."));
            }
            catch (ProxyRemoteInvocationException e)
            {
                tcs.TrySetResult(e);
            }
        };

        await server.StartAsync().Timeout(1);
        await client.ConnectAsync().Timeout(1);

        var exception = await tcs.Task.Timeout(1);
        Assert.That(exception.RemoteErrorCode, Is.EqualTo(7));
        Assert.That(exception.RemoteExceptionType, Is.Null);
        Assert.That(exception.RemoteMessage, Is.Null);
        Assert.That(exception.Message, Does.Not.Contain("client secret"));
    }

    [Test]
    public async Task TypeAndMessage_ThroughClientPool_CarriesTypeAndMessage()
    {
        var serverConfig = CreateServerConfig(Type.Uds);
        serverConfig.RemoteExceptionDetail = RemoteExceptionDetail.TypeAndMessage;
        var (server, _, _) = CreateServerClient(serverConfig, CreateClientConfig(Type.Uds));
        server.OnNexusCreated = nexus => nexus.ServerTaskValueEvent = _ => throw new InvalidOperationException("pooled boom");
        await server.StartAsync().Timeout(1);

        var pool = new NexusClientPool<ClientNexus, ClientNexus.ServerProxy>(
            new NexusClientPoolConfig(CreateClientConfig(Type.Uds)) { MaxConnections = 2 });
        try
        {
            using var rented = await pool.RentClientAsync().Timeout(1);
            var exception = await Throws(
                async () => await rented.Proxy.ServerTaskValue().Timeout(1));
            Assert.That(exception.RemoteExceptionType, Is.EqualTo("System.InvalidOperationException"));
            Assert.That(exception.RemoteMessage, Is.EqualTo("pooled boom"));
        }
        finally
        {
            await pool.DisposeAsync();
        }
    }
}
