using System.Buffers;
using System.Buffers.Binary;
using System.Threading.Channels;
using NexNet.IntegrationTests.TestInterfaces;
using NexNet.Internals;
using NexNet.Interop;
using NexNet.Interop.Hosts;
using NexNet.Invocation;
using NexNet.Messages;
using NexNet.Serialization;
using NexNet.Transports;

#pragma warning disable VSTHRD200

namespace NexNet.IntegrationTests;

/// <summary>
/// Remote exception detail: the opt-in type and message, the application error hook, and the optional third element
/// of an Exception invocation result. End-to-end tests of a client calling the server run on the client matrix (P01b);
/// so do the server-calling-client tests (client options of <see cref="ClientSpec"/>) and the parsing of every detail
/// shape, through frames the server injects. The C# parser and exception constructor tests and the client pool test
/// stay C#-only.
/// </summary>
[ClassDataSource<LanguageHostPool>(Shared = SharedType.PerTestSession)]
internal class RemoteExceptionDetailTests(LanguageHostPool hosts) : MatrixTestBase(hosts)
{
    /// <inheritdoc />
    protected override bool CompleteServerMethodsByDefault => true;

    private const string DefaultMessage = "Exception occurred while executing method on proxy.";

    // ----- Caller side, from the message bodies directly (C#-only: the C# parser; the injected-frame test below runs
    // the same shapes on every client) -----

    private delegate void DetailWriter(ref MsgPackWriter writer);

    private static byte[] ExceptionResultBody(DetailWriter? detail, ushort invocationId = 1)
    {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new MsgPackWriter(buffer);
        writer.WriteArrayHeader(detail == null ? 2 : 3);
        writer.Write(invocationId);
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
        var exception = await ThrowFromBody(ExceptionResultBody(MalformedDetail(shape)));

        await Assert.That(exception.GetType()).IsEqualTo(typeof(ProxyRemoteInvocationException));
        await Assert.That(exception.Message).IsEqualTo(DefaultMessage);
        await Assert.That(exception.RemoteErrorCode).IsNull();
        await Assert.That(exception.RemoteExceptionType).IsNull();
        await Assert.That(exception.RemoteMessage).IsNull();
    }

    private static readonly string[] MalformedShapes =
    [
        "int instead of array", "two-element array", "four-element array", "string code", "int type", "array message",
        "nil detail",
    ];

    private static DetailWriter MalformedDetail(string shape)
    {
        return shape switch
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
    }

    private static DetailWriter OversizedDetail(int typeLength, int messageLength) => (ref MsgPackWriter w) =>
    {
        w.WriteArrayHeader(3);
        w.Write(1);
        w.Write(new string('t', typeLength));
        w.Write(new string('m', messageLength));
    };

    [Test]
    [Arguments(257, 10)]
    [Arguments(10, 2049)]
    public async Task ExceptionResultWithOversizedDetail_ThrowsPlainException(int typeLength, int messageLength)
    {
        var exception = await ThrowFromBody(ExceptionResultBody(OversizedDetail(typeLength, messageLength)));

        await Assert.That(exception.Message).IsEqualTo(DefaultMessage);
        await Assert.That(exception.RemoteErrorCode).IsNull();
        await Assert.That(exception.RemoteExceptionType).IsNull();
        await Assert.That(exception.RemoteMessage).IsNull();
    }

    // ----- Caller side, from frames the server injects into a client under test's session -----

    /// <summary>
    /// Every detail shape of the tests above, parsed by the client under test: the server answers a pending call with
    /// an injected Exception result (H02 frame injection) instead of running it.
    /// </summary>
    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp])]
    public async Task InjectedExceptionResults_ParseLikeTheCSharpClient(ClientTarget target)
    {
        var arrived = Channel.CreateUnbounded<(INexusSession Session, ushort InvocationId)>();
        await using var server = await StartServerAsync(
            // Never answered: the injected result completes the call; the session's end cancels the handler.
            nexus => nexus.ServerTaskValueWithCancellationEvent = async (_, ct) =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                return 0;
            },
            config =>
            {
                config.MaxConcurrentConnectionInvocations = 32;
                config.InvocationCredits = 64;
                ServerTaps.OnReceive(config, (session, frame) =>
                {
                    if (frame.Type == MessageType.Invocation)
                        arrived.Writer.TryWrite((session, TestSerialization.DeserializeMessage<InvocationMessage>(frame.Body).InvocationId));
                });
            });
        await using var client = await ConnectAsync(target, server);

        async Task<ProxyRemoteInvocationException> Inject(DetailWriter? detail)
        {
            var call = Proxy(client).ServerTaskValueWithCancellation(CancellationToken.None).AsTask();
            var (session, id) = await arrived.Reader.ReadAsync().AsTask().Timeout(T(1));
            var body = ExceptionResultBody(detail, id);
            var frame = new byte[3 + body.Length];
            frame[0] = (byte)MessageType.InvocationResult;
            BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(1), (ushort)body.Length);
            body.CopyTo(frame, 3);
            await ((NexusSession<ServerNexus, ServerNexus.ClientProxy>)session).InternalSendRawAsync(frame).AsTask().Timeout(T(1));
            return await Throws(async () => await call.Timeout(T(1)));
        }

        async Task AssertPlain(ProxyRemoteInvocationException exception, string because)
        {
            await Assert.That(exception.Message).IsEqualTo(DefaultMessage).Because(because);
            await Assert.That(exception.RemoteErrorCode).IsNull().Because(because);
            await Assert.That(exception.RemoteExceptionType).IsNull().Because(because);
            await Assert.That(exception.RemoteMessage).IsNull().Because(because);
        }

        await AssertPlain(await Inject(null), "old two-element result");

        var detailed = await Inject((ref MsgPackWriter w) =>
        {
            w.WriteArrayHeader(3);
            w.Write(42);
            w.Write("System.InvalidOperationException");
            w.Write("boom");
        });
        await Assert.That(detailed.RemoteErrorCode).IsEqualTo(42);
        await Assert.That(detailed.RemoteExceptionType).IsEqualTo("System.InvalidOperationException");
        await Assert.That(detailed.RemoteMessage).IsEqualTo("boom");

        var nilFields = await Inject((ref MsgPackWriter w) =>
        {
            w.WriteArrayHeader(3);
            w.Write(7);
            w.WriteNil();
            w.WriteNil();
        });
        await Assert.That(nilFields.RemoteErrorCode).IsEqualTo(7);
        await Assert.That(nilFields.RemoteExceptionType).IsNull();
        await Assert.That(nilFields.RemoteMessage).IsNull();

        foreach (var shape in MalformedShapes)
            await AssertPlain(await Inject(MalformedDetail(shape)), shape);
        await AssertPlain(await Inject(OversizedDetail(257, 10)), "oversized type");
        await AssertPlain(await Inject(OversizedDetail(10, 2049)), "oversized message");

        // Malformed details fail only their call: the session stays up.
        await Assert.That(await Proxy(client).ServerTaskValue().Timeout(T(1))).IsEqualTo(0);
        await AssertState(client, ConnectionState.Connected);
        await EndAsync(client);
    }

    /// <summary>C#-only: the constructor and message of the C# exception type.</summary>
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
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp])]
    public async Task NoneByDefault_ExceptionResultBytesAreUnchanged(ClientTarget target)
    {
        await Assert.That(CreateServerConfig(Type.Uds).RemoteExceptionDetail).IsEqualTo(RemoteExceptionDetail.None);
        await Assert.That(CreateClientConfig(Type.Uds).RemoteExceptionDetail).IsEqualTo(RemoteExceptionDetail.None);

        var results = new List<byte[]>();
        await using var server = await StartServerAsync(
            nexus => nexus.ServerTaskValueEvent = _ => throw new InvalidOperationException("secret"),
            config => ServerTaps.OnSend(config, (_, frame) =>
            {
                if (frame.Type == MessageType.InvocationResult)
                    lock (results)
                        results.Add(frame.Frame);
            }));
        await using var client = await ConnectAsync(target, server);

        var exception = await Throws(
            async () => await Proxy(client).ServerTaskValue().Timeout(T(1)));
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
        await EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp])]
    public async Task TypeAndMessage_ClientCallingServer_CarriesTypeAndMessage(ClientTarget target)
    {
        var fail = true;
        await using var server = await StartServerAsync(
            nexus => nexus.ServerTaskValueEvent = _ => fail ? throw new InvalidOperationException("boom") : ValueTask.FromResult(5),
            config => config.RemoteExceptionDetail = RemoteExceptionDetail.TypeAndMessage);
        await using var client = await ConnectAsync(target, server);

        var exception = await Throws(
            async () => await Proxy(client).ServerTaskValue().Timeout(T(1)));
        await Assert.That(exception.RemoteErrorCode).IsNull();
        await Assert.That(exception.RemoteExceptionType).IsEqualTo("System.InvalidOperationException");
        await Assert.That(exception.RemoteMessage).IsEqualTo("boom");
        await Assert.That(exception.Message).Contains("boom");

        // The session stays usable.
        fail = false;
        await Assert.That(await Proxy(client).ServerTaskValue().Timeout(T(1))).IsEqualTo(5);
        await EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp])]
    public async Task TypeAndMessage_TruncatesLongMessage(ClientTarget target)
    {
        var longMessage = new string('x', 5000);
        await using var server = await StartServerAsync(
            nexus => nexus.ServerTaskValueEvent = _ => throw new InvalidOperationException(longMessage),
            config => config.RemoteExceptionDetail = RemoteExceptionDetail.TypeAndMessage);
        await using var client = await ConnectAsync(target, server);

        var exception = await Throws(
            async () => await Proxy(client).ServerTaskValue().Timeout(T(1)));
        await Assert.That(exception.RemoteExceptionType).IsEqualTo("System.InvalidOperationException");
        await Assert.That(exception.RemoteMessage).IsEqualTo(new string('x', 2048));
        await EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp])]
    public async Task TypeAndMessage_LimitFailureCarriesItsType(ClientTarget target)
    {
        await using var server = await StartServerAsync(configure: config => config.RemoteExceptionDetail = RemoteExceptionDetail.TypeAndMessage);
        await using var client = await ConnectAsync(target, server);

        // Larger than the client accepts (4 MiB by default): the server fails the call with a limit exception.
        var exception = await Throws(
            async () => await Proxy(client).ServerDataResult(5 * 1024 * 1024).Timeout(T(5)));
        await Assert.That(exception.RemoteExceptionType).IsEqualTo(typeof(NexusMessageTooLargeException).FullName);
        await Assert.That(string.IsNullOrEmpty(exception.RemoteMessage)).IsFalse();
        await EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp])]
    public async Task Hook_ResultIsSentWhateverTheSetting(ClientTarget target)
    {
        Exception? seen = null;
        var thrown = new InvalidOperationException("secret");
        await using var server = await StartServerAsync(nexus =>
        {
            nexus.ServerTaskValueEvent = _ => throw thrown;
            nexus.OnInvocationExceptionEvent = (_, exception, _) =>
            {
                seen = exception;
                return new NexusRemoteError(42, "denied");
            };
        });
        await using var client = await ConnectAsync(target, server);

        var exception = await Throws(
            async () => await Proxy(client).ServerTaskValue().Timeout(T(1)));
        await Assert.That(seen).IsSameReferenceAs(thrown);
        await Assert.That(exception.RemoteErrorCode).IsEqualTo(42);
        await Assert.That(exception.RemoteExceptionType).IsNull();
        await Assert.That(exception.RemoteMessage).IsEqualTo("denied");
        await Assert.That(exception.Message).Contains("denied");
        await Assert.That(exception.Message).DoesNotContain("secret");
        await EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp])]
    public async Task Hook_ReturningNull_FallsBackToTheSetting(ClientTarget target)
    {
        await using var server = await StartServerAsync(
            nexus =>
            {
                nexus.ServerTaskValueEvent = _ => throw new ArgumentException("bad");
                nexus.OnInvocationExceptionEvent = (_, _, _) => null;
            },
            config => config.RemoteExceptionDetail = RemoteExceptionDetail.TypeAndMessage);
        await using var client = await ConnectAsync(target, server);

        var exception = await Throws(
            async () => await Proxy(client).ServerTaskValue().Timeout(T(1)));
        await Assert.That(exception.RemoteErrorCode).IsNull();
        await Assert.That(exception.RemoteExceptionType).IsEqualTo("System.ArgumentException");
        await Assert.That(exception.RemoteMessage).IsEqualTo("bad");
        await EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp])]
    public async Task Hook_Throwing_FallsBackToTheSettingAndSessionSurvives(ClientTarget target)
    {
        var fail = true;
        await using var server = await StartServerAsync(
            nexus =>
            {
                nexus.ServerTaskValueEvent = _ => fail ? throw new InvalidOperationException("boom") : ValueTask.FromResult(9);
                nexus.OnInvocationExceptionEvent = (_, _, _) => throw new NotSupportedException("hook failed");
            },
            config => config.RemoteExceptionDetail = RemoteExceptionDetail.TypeAndMessage);
        await using var client = await ConnectAsync(target, server);

        var exception = await Throws(
            async () => await Proxy(client).ServerTaskValue().Timeout(T(1)));
        await Assert.That(exception.RemoteErrorCode).IsNull();
        await Assert.That(exception.RemoteExceptionType).IsEqualTo("System.InvalidOperationException");
        await Assert.That(exception.RemoteMessage).IsEqualTo("boom");

        fail = false;
        await Assert.That(await Proxy(client).ServerTaskValue().Timeout(T(1))).IsEqualTo(9);
        await AssertState(client, ConnectionState.Connected);
        await EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp])]
    public async Task TypeAndMessage_ServerCallingClient_CarriesTypeAndMessage(ClientTarget target)
    {
        await using var server = await StartServerAsync();
        await using var client = await ConnectAsync(target, server, configure: spec => spec.RemoteExceptionDetail = RemoteExceptionDetail.TypeAndMessage);
        Script(client).ClientTaskValue.Throws(ErrorKind.InvalidOperation, "client boom");

        ProxyRemoteInvocationException exception;
        using (var context = server.Server.ContextProvider.Rent())
        {
            exception = await Throws(
                async () => await context.Proxy.Client(FirstSessionId(server)).ClientTaskValue().Timeout(T(1)));
        }

        // The type name is the client language's own (System.InvalidOperationException in C#).
        if (target.Language == ClientLanguages.CSharp)
            await Assert.That(exception.RemoteExceptionType).IsEqualTo("System.InvalidOperationException");
        else
            await Assert.That(string.IsNullOrEmpty(exception.RemoteExceptionType)).IsFalse();
        await Assert.That(exception.RemoteErrorCode).IsNull();
        await Assert.That(exception.RemoteMessage).IsEqualTo("client boom");
        await EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp])]
    public async Task Hook_ServerCallingClient_CarriesApplicationError(ClientTarget target)
    {
        await using var server = await StartServerAsync();
        await using var client = await ConnectAsync(target, server, configure: spec => spec.OnInvocationException = new NexusRemoteError(7, null));
        Script(client).ClientTaskValue.Throws(ErrorKind.InvalidOperation, "client secret");

        ProxyRemoteInvocationException exception;
        using (var context = server.Server.ContextProvider.Rent())
        {
            exception = await Throws(
                async () => await context.Proxy.Client(FirstSessionId(server)).ClientTaskValue().Timeout(T(1)));
        }

        await Assert.That(exception.RemoteErrorCode).IsEqualTo(7);
        await Assert.That(exception.RemoteExceptionType).IsNull();
        await Assert.That(exception.RemoteMessage).IsNull();
        await Assert.That(exception.Message).DoesNotContain("client secret");
        await EndAsync(client);
    }

    /// <summary>C#-only: the C# client pool (client pools are ported by P01g).</summary>
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