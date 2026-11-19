using System.Buffers;
using NexNet.IntegrationTests.TestInterfaces;
using NexNet.Internals;
using NexNet.Invocation;
using NexNet.Messages;
using NexNet.Serialization;
using NexNet.Transports;

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
        => (await Assert.ThrowsExactlyAsync<ProxyRemoteInvocationException>(call))!;

    private static async Task<ProxyRemoteInvocationException> ThrowFromBody(byte[] body)
    {
        var message = new InvocationResultMessage();
        var reader = new MsgPackReader(new ReadOnlyMemory<byte>(body), NexusSerializerOptions.Untrusted);
        message.Deserialize(ref reader);

        var state = RegisteredInvocationState<int>.Rent();
        state.Result = message;
        await Assert.That(state.TrySetResult()).IsTrue();

        var valueTask = new ValueTask<int>(state, state.Version);
        return await Throws(async () => await valueTask);
    }

    [Test]
    public async Task OldTwoElementExceptionResult_ThrowsPlainException()
    {
        var exception = await ThrowFromBody(ExceptionResultBody(null));

        await Assert.That(exception.GetType()).IsEqualTo(typeof(ProxyRemoteInvocationException));
        await Assert.That(exception.Message).IsEqualTo(DefaultMessage);
        await Assert.That(exception.RemoteErrorCode).IsNull();
        await Assert.That(exception.RemoteExceptionType).IsNull();
        await Assert.That(exception.RemoteMessage).IsNull();
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

        await Assert.That(exception.RemoteErrorCode).IsEqualTo(42);
        await Assert.That(exception.RemoteExceptionType).IsEqualTo("System.InvalidOperationException");
        await Assert.That(exception.RemoteMessage).IsEqualTo("boom");
        await Assert.That(exception.Message).StartsWith(DefaultMessage);
        await Assert.That(exception.Message).Contains("boom");
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

        await Assert.That(exception.RemoteErrorCode).IsEqualTo(7);
        await Assert.That(exception.RemoteExceptionType).IsNull();
        await Assert.That(exception.RemoteMessage).IsNull();
        await Assert.That(exception.Message).StartsWith(DefaultMessage);
    }

    // Well-formed MessagePack values that are not a valid detail.
    [Test]
    [Arguments("int instead of array")]
    [Arguments("two-element array")]
    [Arguments("four-element array")]
    [Arguments("string code")]
    [Arguments("int type")]
    [Arguments("array message")]
    [Arguments("nil detail")]
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

        await Assert.That(exception.GetType()).IsEqualTo(typeof(ProxyRemoteInvocationException));
        await Assert.That(exception.Message).IsEqualTo(DefaultMessage);
        await Assert.That(exception.RemoteErrorCode).IsNull();
        await Assert.That(exception.RemoteExceptionType).IsNull();
        await Assert.That(exception.RemoteMessage).IsNull();
    }

    [Test]
    [Arguments(257, 10)]
    [Arguments(10, 2049)]
    public async Task ExceptionResultWithOversizedDetail_ThrowsPlainException(int typeLength, int messageLength)
    {
        var exception = await ThrowFromBody(ExceptionResultBody((ref MsgPackWriter w) =>
        {
            w.WriteArrayHeader(3);
            w.Write(1);
            w.Write(new string('t', typeLength));
            w.Write(new string('m', messageLength));
        }));

        await Assert.That(exception.Message).IsEqualTo(DefaultMessage);
        await Assert.That(exception.RemoteErrorCode).IsNull();
        await Assert.That(exception.RemoteExceptionType).IsNull();
        await Assert.That(exception.RemoteMessage).IsNull();
    }

    [Test]
    public async Task ExceptionConstructedWithDetail_IncludesRemoteMessage()
    {
        var exception = new ProxyRemoteInvocationException(42, "System.InvalidOperationException", "boom");

        await Assert.That(exception.RemoteErrorCode).IsEqualTo(42);
        await Assert.That(exception.RemoteExceptionType).IsEqualTo("System.InvalidOperationException");
        await Assert.That(exception.RemoteMessage).IsEqualTo("boom");
        await Assert.That(exception.Message).IsEqualTo(DefaultMessage + " System.InvalidOperationException: boom (code 42)");

        await Assert.That(new ProxyRemoteInvocationException(null, "System.InvalidOperationException", "boom").Message).IsEqualTo(DefaultMessage + " System.InvalidOperationException: boom");
        await Assert.That(new ProxyRemoteInvocationException(42, null, "denied").Message).IsEqualTo(DefaultMessage + " denied (code 42)");
        await Assert.That(new ProxyRemoteInvocationException(7, null, null).Message).IsEqualTo(DefaultMessage + " (code 7)");
        await Assert.That(new ProxyRemoteInvocationException(null, null, null).Message).IsEqualTo(DefaultMessage);

        var plain = new ProxyRemoteInvocationException();
        await Assert.That(plain.Message).IsEqualTo(DefaultMessage);
        await Assert.That(plain.RemoteErrorCode).IsNull();
        await Assert.That(plain.RemoteExceptionType).IsNull();
        await Assert.That(plain.RemoteMessage).IsNull();
    }

    // ----- End to end -----

    [Test]
    [Transports(Type.Uds, Type.Tcp)]
    public async Task NoneByDefault_ExceptionResultBytesAreUnchanged(Type type)
    {
        var serverConfig = CreateServerConfig(type);
        await Assert.That(serverConfig.RemoteExceptionDetail).IsEqualTo(RemoteExceptionDetail.None);
        await Assert.That(CreateClientConfig(type).RemoteExceptionDetail).IsEqualTo(RemoteExceptionDetail.None);

        var results = new List<byte[]>();
        ServerTaps.OnSend(serverConfig, (_, frame) =>
        {
            if (frame.Type == MessageType.InvocationResult)
                lock (results)
                    results.Add(frame.Frame);
        });

        var (server, client, _) = CreateServerClient(serverConfig, CreateClientConfig(type));
        server.OnNexusCreated = nexus => nexus.ServerTaskValueEvent = _ => throw new InvalidOperationException("secret");

        await server.StartAsync().Timeout(T(TestTimeouts.Connect));
        await client.ConnectAsync().Timeout(T(TestTimeouts.Connect));

        var exception = await Throws(
            async () => await client.Proxy.ServerTaskValue().Timeout(T(1)));
        await Assert.That(exception.Message).IsEqualTo(DefaultMessage);
        await Assert.That(exception.RemoteErrorCode).IsNull();
        await Assert.That(exception.RemoteExceptionType).IsNull();
        await Assert.That(exception.RemoteMessage).IsNull();

        byte[][] sent;
        lock (results)
            sent = results.ToArray();

        await Assert.That(sent).Count().IsEqualTo(1);
        var frame = sent[0];

        // Header: type (1), body length (u16 LE); body: fixarray(2), invocation id (positive fixint), state 2.
        var body = frame.AsSpan(3).ToArray();
        await Assert.That(BitConverter.ToUInt16(frame, 1)).IsEqualTo(body.Length);
        await Assert.That(body).Count().IsEqualTo(3);
        await Assert.That(body[0]).IsEqualTo(0x92);
        await Assert.That(body[2]).IsEqualTo((byte)InvocationResultMessage.StateType.Exception);
    }

    [Test]
    [Transports(Type.Uds, Type.Tcp)]
    public async Task TypeAndMessage_ClientCallingServer_CarriesTypeAndMessage(Type type)
    {
        var serverConfig = CreateServerConfig(type);
        serverConfig.RemoteExceptionDetail = RemoteExceptionDetail.TypeAndMessage;
        var (server, client, _) = CreateServerClient(serverConfig, CreateClientConfig(type));
        server.OnNexusCreated = nexus => nexus.ServerTaskValueEvent = _ => throw new InvalidOperationException("boom");

        await server.StartAsync().Timeout(T(TestTimeouts.Connect));
        await client.ConnectAsync().Timeout(T(TestTimeouts.Connect));

        var exception = await Throws(
            async () => await client.Proxy.ServerTaskValue().Timeout(T(1)));
        await Assert.That(exception.RemoteErrorCode).IsNull();
        await Assert.That(exception.RemoteExceptionType).IsEqualTo("System.InvalidOperationException");
        await Assert.That(exception.RemoteMessage).IsEqualTo("boom");
        await Assert.That(exception.Message).Contains("boom");

        // The session stays usable.
        server.NexusCreatedQueue.TryPeek(out var serverNexus);
        serverNexus!.ServerTaskValueEvent = _ => ValueTask.FromResult(5);
        await Assert.That(await client.Proxy.ServerTaskValue().Timeout(T(1))).IsEqualTo(5);
    }

    [Test]
    [Transports(Type.Uds, Type.Tcp)]
    public async Task TypeAndMessage_TruncatesLongMessage(Type type)
    {
        var serverConfig = CreateServerConfig(type);
        serverConfig.RemoteExceptionDetail = RemoteExceptionDetail.TypeAndMessage;
        var (server, client, _) = CreateServerClient(serverConfig, CreateClientConfig(type));
        var longMessage = new string('x', 5000);
        server.OnNexusCreated = nexus => nexus.ServerTaskValueEvent = _ => throw new InvalidOperationException(longMessage);

        await server.StartAsync().Timeout(T(TestTimeouts.Connect));
        await client.ConnectAsync().Timeout(T(TestTimeouts.Connect));

        var exception = await Throws(
            async () => await client.Proxy.ServerTaskValue().Timeout(T(1)));
        await Assert.That(exception.RemoteExceptionType).IsEqualTo("System.InvalidOperationException");
        await Assert.That(exception.RemoteMessage).IsEqualTo(new string('x', 2048));
    }

    [Test]
    [Transports(Type.Uds, Type.Tcp)]
    public async Task TypeAndMessage_LimitFailureCarriesItsType(Type type)
    {
        var serverConfig = CreateServerConfig(type);
        serverConfig.RemoteExceptionDetail = RemoteExceptionDetail.TypeAndMessage;
        var (server, client, clientNexus) = CreateServerClient(serverConfig, CreateClientConfig(type));

        await server.StartAsync().Timeout(T(TestTimeouts.Connect));
        await client.ConnectAsync().Timeout(T(TestTimeouts.Connect));

        // Larger than the client accepts (4 MiB by default): the server fails the call with a limit exception.
        var exception = await Throws(
            async () => await clientNexus.Context.Proxy.ServerDataResult(5 * 1024 * 1024).Timeout(T(5)));
        await Assert.That(exception.RemoteExceptionType).IsEqualTo(typeof(NexusMessageTooLargeException).FullName);
        await Assert.That(string.IsNullOrEmpty(exception.RemoteMessage)).IsFalse();
    }

    [Test]
    [Transports(Type.Uds, Type.Tcp)]
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

        await server.StartAsync().Timeout(T(TestTimeouts.Connect));
        await client.ConnectAsync().Timeout(T(TestTimeouts.Connect));

        var exception = await Throws(
            async () => await client.Proxy.ServerTaskValue().Timeout(T(1)));
        await Assert.That(seen).IsSameReferenceAs(thrown);
        await Assert.That(exception.RemoteErrorCode).IsEqualTo(42);
        await Assert.That(exception.RemoteExceptionType).IsNull();
        await Assert.That(exception.RemoteMessage).IsEqualTo("denied");
        await Assert.That(exception.Message).Contains("denied");
        await Assert.That(exception.Message).DoesNotContain("secret");
    }

    [Test]
    [Transports(Type.Uds, Type.Tcp)]
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

        await server.StartAsync().Timeout(T(TestTimeouts.Connect));
        await client.ConnectAsync().Timeout(T(TestTimeouts.Connect));

        var exception = await Throws(
            async () => await client.Proxy.ServerTaskValue().Timeout(T(1)));
        await Assert.That(exception.RemoteErrorCode).IsNull();
        await Assert.That(exception.RemoteExceptionType).IsEqualTo("System.ArgumentException");
        await Assert.That(exception.RemoteMessage).IsEqualTo("bad");
    }

    [Test]
    [Transports(Type.Uds, Type.Tcp)]
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

        await server.StartAsync().Timeout(T(TestTimeouts.Connect));
        await client.ConnectAsync().Timeout(T(TestTimeouts.Connect));

        var exception = await Throws(
            async () => await client.Proxy.ServerTaskValue().Timeout(T(1)));
        await Assert.That(exception.RemoteErrorCode).IsNull();
        await Assert.That(exception.RemoteExceptionType).IsEqualTo("System.InvalidOperationException");
        await Assert.That(exception.RemoteMessage).IsEqualTo("boom");

        fail = false;
        await Assert.That(await client.Proxy.ServerTaskValue().Timeout(T(1))).IsEqualTo(9);
        await Assert.That(client.State).IsEqualTo(ConnectionState.Connected);
    }

    [Test]
    [Transports(Type.Uds, Type.Tcp)]
    public async Task TypeAndMessage_ServerCallingClient_CarriesTypeAndMessage(Type type)
    {
        var clientConfig = CreateClientConfig(type);
        clientConfig.RemoteExceptionDetail = RemoteExceptionDetail.TypeAndMessage;
        var (server, client, clientNexus) = CreateServerClient(CreateServerConfig(type), clientConfig);
        clientNexus.ClientTaskValueEvent = _ => throw new InvalidOperationException("client boom");

        await server.StartAsync().Timeout(T(TestTimeouts.Connect));
        await client.ConnectAsync().Timeout(T(TestTimeouts.Connect));

        using var context = server.Server.ContextProvider.Rent();
        var exception = await Throws(
            async () => await context.Proxy.Client(context.Proxy.GetIds().First()).ClientTaskValue().Timeout(T(1)));
        await Assert.That(exception.RemoteExceptionType).IsEqualTo("System.InvalidOperationException");
        await Assert.That(exception.RemoteMessage).IsEqualTo("client boom");
    }

    [Test]
    [Transports(Type.Uds, Type.Tcp)]
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
                tcs.TrySetException(new InvalidOperationException("The call did not fail."));
            }
            catch (ProxyRemoteInvocationException e)
            {
                tcs.TrySetResult(e);
            }
        };

        await server.StartAsync().Timeout(T(TestTimeouts.Connect));
        await client.ConnectAsync().Timeout(T(TestTimeouts.Connect));

        var exception = await tcs.Task.Timeout(T(1));
        await Assert.That(exception.RemoteErrorCode).IsEqualTo(7);
        await Assert.That(exception.RemoteExceptionType).IsNull();
        await Assert.That(exception.RemoteMessage).IsNull();
        await Assert.That(exception.Message).DoesNotContain("client secret");
    }

    [Test]
    public async Task TypeAndMessage_ThroughClientPool_CarriesTypeAndMessage()
    {
        var serverConfig = CreateServerConfig(Type.Uds);
        serverConfig.RemoteExceptionDetail = RemoteExceptionDetail.TypeAndMessage;
        var (server, _, _) = CreateServerClient(serverConfig, CreateClientConfig(Type.Uds));
        server.OnNexusCreated = nexus => nexus.ServerTaskValueEvent = _ => throw new InvalidOperationException("pooled boom");
        await server.StartAsync().Timeout(T(TestTimeouts.Connect));

        var pool = new NexusClientPool<ClientNexus, ClientNexus.ServerProxy>(
            new NexusClientPoolConfig(CreateClientConfig(Type.Uds)) { MaxConnections = 2 });
        try
        {
            using var rented = await pool.RentClientAsync().Timeout(T(1));
            var exception = await Throws(
                async () => await rented.Proxy.ServerTaskValue().Timeout(T(1)));
            await Assert.That(exception.RemoteExceptionType).IsEqualTo("System.InvalidOperationException");
            await Assert.That(exception.RemoteMessage).IsEqualTo("pooled boom");
        }
        finally
        {
            await pool.DisposeAsync();
        }
    }
}