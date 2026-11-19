using System.Buffers;
using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using NexNet.Internals;
using NexNet.Invocation;
using NexNet.Messages;
using NexNet.Serialization;
using NexNet.TestNexus.Interop;
using NexNet.Transports;

#pragma warning disable VSTHRD200

namespace NexNet.IntegrationTests.Interop;

/// <summary>
/// Smoke tests of the interop type zoo (NexNet.TestNexus) with the C# client over UDS, and of the server's raw frame
/// injection hook (<c>NexusSession.InternalSendRawAsync</c>).
/// </summary>
internal class InteropZooTests : BaseTests
{
    private sealed record Configs(ServerConfig Server, ClientConfig Client);

    private async Task<(NexusClient<InteropClientNexus, InteropClientNexus.ServerProxy> Client, InteropClientNexus Nexus)> ConnectAsync(
        Action<InteropServerNexus>? nexusCreated = null,
        Action<Configs>? configure = null)
    {
        var configs = new Configs(CreateServerConfig(Type.Uds), CreateClientConfig(Type.Uds));
        configure?.Invoke(configs);
        var server = CreateServer<InteropServerNexus, InteropServerNexus.ClientProxy>(configs.Server, nexusCreated);
        var (client, nexus) = CreateClient<InteropClientNexus, InteropClientNexus.ServerProxy>(configs.Client);
        await server.StartAsync().Timeout(1);
        await client.ConnectAsync().Timeout(1);
        return (client, nexus);
    }

    private static byte[] Bytes<T>(T value) => NexusSerializer.Serialize(value);

    [Test]
    public async Task EchoesBuiltInTypes()
    {
        var (client, _) = await ConnectAsync();
        var proxy = client.Proxy;

        await Assert.That(await proxy.EchoBool(true)).IsTrue();
        await Assert.That(await proxy.EchoSByte(sbyte.MinValue)).IsEqualTo(sbyte.MinValue);
        await Assert.That(await proxy.EchoByte(byte.MaxValue)).IsEqualTo(byte.MaxValue);
        await Assert.That(await proxy.EchoInt16(short.MinValue)).IsEqualTo(short.MinValue);
        await Assert.That(await proxy.EchoUInt16(ushort.MaxValue)).IsEqualTo(ushort.MaxValue);
        await Assert.That(await proxy.EchoInt32(int.MinValue)).IsEqualTo(int.MinValue);
        await Assert.That(await proxy.EchoUInt32(uint.MaxValue)).IsEqualTo(uint.MaxValue);
        await Assert.That(await proxy.EchoInt64(long.MinValue)).IsEqualTo(long.MinValue);
        await Assert.That(await proxy.EchoUInt64(ulong.MaxValue)).IsEqualTo(ulong.MaxValue);
        await Assert.That(BitConverter.SingleToInt32Bits(await proxy.EchoSingle(-0f))).IsEqualTo(BitConverter.SingleToInt32Bits(-0f));
        await Assert.That(double.IsNaN(await proxy.EchoDouble(double.NaN))).IsTrue();
        await Assert.That(await proxy.EchoHalf(Half.MaxValue)).IsEqualTo(Half.MaxValue);
        var loneSurrogate = (char)0xD800;
        await Assert.That(await proxy.EchoChar(loneSurrogate)).IsEqualTo(loneSurrogate);

        var decimalValue = await proxy.EchoDecimal(-1.50m);
        await Assert.That(decimal.GetBits(decimalValue)).IsEquivalentTo(decimal.GetBits(-1.50m));

        var text = "h" + (char)0xE9 + "llo " + char.ConvertFromUtf32(0x1F600);
        await Assert.That(await proxy.EchoString(text)).IsEqualTo(text);
        await Assert.That(await proxy.EchoString(null)).IsNull();
        await Assert.That(await proxy.EchoBytes([1, 2, 3])).IsEquivalentTo(new byte[] { 1, 2, 3 });
        await Assert.That((await proxy.EchoMemory(new byte[] { 4, 5 })).ToArray()).IsEquivalentTo(new byte[] { 4, 5 });
        await Assert.That((await proxy.EchoReadOnlyMemory(new byte[] { 6 })).ToArray()).IsEquivalentTo(new byte[] { 6 });
        await Assert.That((await proxy.EchoReadOnlySequence(new ReadOnlySequence<byte>(new byte[] { 7, 8 }))).ToArray()).IsEquivalentTo(new byte[] { 7, 8 });
        await Assert.That((await proxy.EchoArraySegment(new ArraySegment<byte>([0, 9, 10, 0], 1, 2))).ToArray()).IsEquivalentTo(new byte[] { 9, 10 });

        var utc = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var echoedUtc = await proxy.EchoDateTime(utc);
        await Assert.That(echoedUtc).IsEqualTo(utc);
        await Assert.That(echoedUtc.Kind).IsEqualTo(DateTimeKind.Utc);
        await Assert.That((await proxy.EchoDateTime(new DateTime(2020, 1, 1))).Kind).IsEqualTo(DateTimeKind.Unspecified);

        var offset = new DateTimeOffset(2021, 6, 1, 12, 30, 0, TimeSpan.FromMinutes(-150));
        var echoedOffset = await proxy.EchoDateTimeOffset(offset);
        await Assert.That(echoedOffset).IsEqualTo(offset);
        await Assert.That(echoedOffset.Offset).IsEqualTo(offset.Offset);
        await Assert.That(await proxy.EchoTimeSpan(TimeSpan.MinValue)).IsEqualTo(TimeSpan.MinValue);
        await Assert.That(await proxy.EchoDateOnly(DateOnly.MaxValue)).IsEqualTo(DateOnly.MaxValue);
        await Assert.That(await proxy.EchoTimeOnly(TimeOnly.MaxValue)).IsEqualTo(TimeOnly.MaxValue);

        var guid = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
        await Assert.That(await proxy.EchoGuid(guid)).IsEqualTo(guid);
        var big = -BigInteger.Pow(2, 100) + 1;
        await Assert.That(await proxy.EchoBigInteger(big)).IsEqualTo(big);
        await Assert.That(await proxy.EchoUri(new Uri("https://example.com/a?b=c"))).IsEqualTo(new Uri("https://example.com/a?b=c"));
        await Assert.That(await proxy.EchoVersion(new Version(1, 2, 3, 4))).IsEqualTo(new Version(1, 2, 3, 4));
        await Assert.That(await proxy.EchoValueTuple((1, "two", 3.0))).IsEqualTo((1, "two", 3.0));
        var tuple7 = (1, 2L, "three", true, 5.5, guid, (byte)7);
        await Assert.That(await proxy.EchoValueTuple7(tuple7)).IsEqualTo(tuple7);
        await Assert.That(await proxy.EchoTuple(Tuple.Create(1, "one"))).IsEqualTo(Tuple.Create(1, "one"));
        await Assert.That(await proxy.EchoKeyValuePair(new KeyValuePair<string, int>("k", 1))).IsEqualTo(new KeyValuePair<string, int>("k", 1));
    }

    [Test]
    public async Task EchoesPrimitiveArraysContainersAndEnums()
    {
        var (client, _) = await ConnectAsync();
        var proxy = client.Proxy;

        await Assert.That(await proxy.EchoSByteArray([sbyte.MinValue, 0, sbyte.MaxValue])).IsEquivalentTo(new[] { sbyte.MinValue, (sbyte)0, sbyte.MaxValue });
        await Assert.That(await proxy.EchoInt16Array([short.MinValue, short.MaxValue])).IsEquivalentTo(new[] { short.MinValue, short.MaxValue });
        await Assert.That(await proxy.EchoUInt16Array([ushort.MaxValue])).IsEquivalentTo(new[] { ushort.MaxValue });
        await Assert.That(await proxy.EchoInt32Array([])).IsEmpty();
        await Assert.That(await proxy.EchoUInt32Array([uint.MaxValue])).IsEquivalentTo(new[] { uint.MaxValue });
        await Assert.That(await proxy.EchoInt64Array([long.MinValue])).IsEquivalentTo(new[] { long.MinValue });
        await Assert.That(await proxy.EchoUInt64Array([ulong.MaxValue])).IsEquivalentTo(new[] { ulong.MaxValue });
        await Assert.That(await proxy.EchoSingleArray([1.5f])).IsEquivalentTo(new[] { 1.5f });
        await Assert.That(await proxy.EchoDoubleArray([double.MaxValue])).IsEquivalentTo(new[] { double.MaxValue });
        await Assert.That(await proxy.EchoCharArray(['a', char.MaxValue])).IsEquivalentTo(new[] { 'a', char.MaxValue });
        await Assert.That(await proxy.EchoHalfArray([Half.One])).IsEquivalentTo(new[] { Half.One });
        await Assert.That(await proxy.EchoInt32List([1, 2, 3])).IsEquivalentTo(new List<int> { 1, 2, 3 });
        await Assert.That((await proxy.EchoInt64Memory(new long[] { 9 })).ToArray()).IsEquivalentTo(new long[] { 9 });
        await Assert.That((await proxy.EchoDoubleReadOnlyMemory(new double[] { 0.5 })).ToArray()).IsEquivalentTo(new[] { 0.5 });
        await Assert.That(await proxy.EchoInt32Array(null)).IsNull();

        await Assert.That(await proxy.EchoStringSet(["a", "b"])).IsEquivalentTo(new HashSet<string> { "a", "b" });
        await Assert.That((await proxy.EchoInt32Queue(new Queue<int>([1, 2, 3])))!.ToArray()).IsEquivalentTo(new[] { 1, 2, 3 });
        await Assert.That((await proxy.EchoStringStack(new Stack<string>(["bottom", "top"])))!.ToArray()).IsEquivalentTo(new[] { "top", "bottom" });

        var byGuid = new Dictionary<Guid, long> { [Guid.NewGuid()] = 1, [Guid.NewGuid()] = long.MaxValue };
        await Assert.That(await proxy.EchoGuidDictionary(byGuid)).IsEquivalentTo(byGuid);
        var byInt = new Dictionary<int, InteropItem> { [1] = new(1, "one"), [-2] = new(-2, null) };
        await Assert.That(await proxy.EchoInt32Dictionary(byInt)).IsEquivalentTo(byInt);
        var nested = new Dictionary<string, List<int[]>> { ["x"] = [[1, 2], []] };
        await Assert.That(Bytes(await proxy.EchoNestedContainers(nested))).IsEquivalentTo(Bytes(nested));

        await Assert.That(await proxy.EchoNullableInt32(null)).IsNull();
        await Assert.That(await proxy.EchoNullableGuid(Guid.Empty)).IsEqualTo(Guid.Empty);
        await Assert.That(await proxy.EchoNullableInt32List([1, null, 3])).IsEquivalentTo(new List<int?> { 1, null, 3 });
        await Assert.That(await proxy.EchoFlags(InteropFlags.All)).IsEqualTo(InteropFlags.All);
        await Assert.That(await proxy.EchoLongEnum(InteropLongEnum.BeyondDouble)).IsEqualTo(InteropLongEnum.BeyondDouble);
        await Assert.That(await proxy.EchoEnum(InteropEnum.Min)).IsEqualTo(InteropEnum.Min);
    }

    [Test]
    public async Task EchoesObjectsUnionsGenericsAndUserFormatter()
    {
        var (client, _) = await ConnectAsync();
        var proxy = client.Proxy;

        var all = new InteropAllTypes
        {
            BoolValue = true,
            SByteValue = -1,
            ByteValue = 200,
            Int16Value = -300,
            UInt16Value = 60000,
            Int32Value = -70000,
            UInt32Value = 4_000_000_000,
            Int64Value = long.MinValue,
            UInt64Value = ulong.MaxValue,
            SingleValue = 1.25f,
            DoubleValue = -2.5,
            HalfValue = (Half)0.5,
            CharValue = 'Z',
            DecimalValue = 12.345m,
            StringValue = "text",
            BytesValue = [1, 2],
            DateTimeValue = new DateTime(2000, 2, 29, 1, 2, 3, DateTimeKind.Utc),
            DateTimeOffsetValue = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.FromHours(14)),
            TimeSpanValue = TimeSpan.FromTicks(-1),
            DateOnlyValue = new DateOnly(1, 1, 1),
            TimeOnlyValue = new TimeOnly(23, 59),
            GuidValue = Guid.NewGuid(),
            BigIntegerValue = BigInteger.Pow(10, 30),
            UriValue = new Uri("urn:interop"),
            VersionValue = new Version(2, 0),
            EnumValue = InteropEnum.Negative,
            FlagsValue = InteropFlags.A | InteropFlags.High,
            LongEnumValue = InteropLongEnum.Max,
            NullableInt32Value = null,
            Int32ArrayValue = [1, -1],
            StringListValue = ["a"],
            StringMapValue = new() { ["k"] = 1 },
        };
        await Assert.That(Bytes(await proxy.EchoAllTypes(all))).IsEquivalentTo(Bytes(all));
        await Assert.That(await proxy.EchoAllTypes(null)).IsNull();

        var point = new InteropStruct { X = 1, Y = 2.5, Label = "p" };
        await Assert.That(await proxy.EchoStruct(point)).IsEqualTo(point);
        await Assert.That(await proxy.EchoNullableStruct(null)).IsNull();
        await Assert.That(await proxy.EchoRecord(new InteropRecord("r", 3, point))).IsEqualTo(new InteropRecord("r", 3, point));
        var recordStruct = new InteropRecordStruct(long.MaxValue, Guid.NewGuid());
        await Assert.That(await proxy.EchoRecordStruct(recordStruct)).IsEqualTo(recordStruct);

        var sparse = new InteropSparse { First = 1, Fourth = "four" };
        var sparseBytes = Bytes(sparse);
        await Assert.That(sparseBytes[0]).IsEqualTo((byte)0x94); // fixarray(4): keys 1 and 2 are nil
        await Assert.That(Bytes(await proxy.EchoSparse(sparse))).IsEquivalentTo(sparseBytes);

        var shape = await proxy.EchoShape(new InteropRectangle { Width = 2, Height = 3 });
        await Assert.That(shape is InteropRectangle { Width: 2, Height: 3 }).IsTrue();
        var shapes = await proxy.EchoShapes([new InteropCircle { Radius = 1 }, new InteropRectangle()]);
        await Assert.That(shapes!.Select(s => s.GetType()).ToArray()).IsEquivalentTo(new[] { typeof(InteropCircle), typeof(InteropRectangle) });
        var message = await proxy.EchoMessage(new InteropBinaryMessage { Data = [1], Flags = InteropFlags.B });
        await Assert.That(message is InteropBinaryMessage { Flags: InteropFlags.B }).IsTrue();
        await Assert.That(await proxy.EchoMessage(null)).IsNull();

        var nestedValue = new InteropNested
        {
            Record = new InteropRecord("n", 1, point),
            Items = [new InteropItem(1, "a")],
            ByIndex = new() { [0] = point },
            Shape = new InteropCircle { Radius = 4 },
            Child = new InteropNested(),
        };
        await Assert.That(Bytes(await proxy.EchoNested(nestedValue))).IsEquivalentTo(Bytes(nestedValue));

        await Assert.That((await proxy.EchoStringBox(new InteropBox<string> { Value = "boxed" }))!.Value).IsEqualTo("boxed");
        await Assert.That((await proxy.EchoItemBox(new InteropBox<InteropItem> { Value = new(5, "five") }))!.Value).IsEqualTo(new InteropItem(5, "five"));
        var pair = await proxy.EchoPair(new InteropPair<int, InteropItem> { First = 7, Second = [new(1, null)] });
        await Assert.That(pair!.First).IsEqualTo(7);
        await Assert.That(pair.Second).IsEquivalentTo(new List<InteropItem> { new(1, null) });

        await Assert.That(await proxy.EchoPoint(new InteropPoint(-1, 2))).IsEqualTo(new InteropPoint(-1, 2));
        await Assert.That(Bytes(new InteropPoint(1, 2))).IsEquivalentTo(new byte[] { 0x92, 0x01, 0x02 });
        await Assert.That(await proxy.EchoPoints([new(1, 1), new(2, 2)])).IsEquivalentTo(new List<InteropPoint> { new(1, 1), new(2, 2) });

        await Assert.That(await proxy.Combine("c", 2, point)).IsEqualTo(new InteropRecord("c", 2, point));
    }

    [Test]
    public async Task LargeValuesPipesChannelsAndCallbacks()
    {
        var (client, clientNexus) = await ConnectAsync();
        var proxy = client.Proxy;

        // Above one 64 KiB frame in both directions.
        var large = await proxy.CreateBytes(200_000, 3).AsTask().Timeout(5);
        await Assert.That(large).IsEquivalentTo(InteropServerNexus.Pattern(200_000, 3));
        await Assert.That(await proxy.HashBytes(large).AsTask().Timeout(5)).IsEqualTo(InteropServerNexus.Hash(large));
        await Assert.That(await proxy.EchoBytes(large).AsTask().Timeout(5)).IsEquivalentTo(large);

        var pipe = clientNexus.Context.CreatePipe();
        await proxy.EchoPipe(pipe).Timeout(1);
        await pipe.ReadyTask.Timeout(1);
        var data = InteropServerNexus.Pattern(100_000, 9);
        await pipe.Output.WriteAsync(data);
        await pipe.Output.CompleteAsync();
        var received = new List<byte>();
        while (true)
        {
            var result = await pipe.Input.ReadAsync().AsTask().Timeout(5);
            received.AddRange(result.Buffer.ToArray());
            pipe.Input.AdvanceTo(result.Buffer.End);
            if (result.IsCompleted)
                break;
        }

        await Assert.That(received.ToArray()).IsEquivalentTo(data);

        var channel = client.CreateChannel<InteropItem>();
        await proxy.EchoItemChannel(channel).Timeout(1);
        var items = Enumerable.Range(0, 50).Select(i => new InteropItem(i, "item" + i)).ToList();
        var writer = await channel.GetWriterAsync().AsTask().Timeout(1);
        await writer.WriteAsync(items);
        await writer.CompleteAsync();
        var echoed = new List<InteropItem>();
        await foreach (var item in await channel.GetReaderAsync())
            echoed.Add(item);
        await Assert.That(echoed).IsEquivalentTo(items);

        await Assert.That(await proxy.CallClientEchoString("back").AsTask().Timeout(1)).IsEqualTo("back");
        await proxy.CallClientNotify("hello").Timeout(1);
        proxy.Notify("one");
        await proxy.Ping().Timeout(1);
        await Assert.That(await proxy.TakeNotifications().AsTask().Timeout(1)).IsEquivalentTo(new[] { "one" });
        await Assert.That(clientNexus.Notifications.ToArray()).IsEquivalentTo(new[] { "hello" });
        await Assert.That(await proxy.SlowEcho(4, 10).AsTask().Timeout(1)).IsEqualTo(4);
    }

    [Test]
    public async Task AuthorizesByIdentity()
    {
        var (client, _) = await ConnectAsync(
            nexus => nexus.TokenValidator = (token, _) => ValueTask.FromResult<IIdentity?>(
                new DefaultIdentity { DisplayName = token == null ? null : Encoding.UTF8.GetString(token.Value.Span) }),
            configs =>
            {
                configs.Server.Authenticate = true;
                configs.Client.Authenticate = () => Encoding.UTF8.GetBytes(InteropServerNexus.ReaderIdentity);
            });

        await Assert.That(await client.Proxy.GetIdentity().AsTask().Timeout(1)).IsEqualTo(InteropServerNexus.ReaderIdentity);
        await Assert.That(await client.Proxy.ReadSecret().AsTask().Timeout(1)).IsEqualTo(InteropServerNexus.Secret);
        await Assert.ThrowsExactlyAsync<ProxyUnauthorizedException>(async () => await client.Proxy.AdminEcho(1).AsTask().Timeout(1));
    }

    [Test]
    public async Task VersionOneClientConnectsToVersionTwoServer()
    {
        var server = CreateServer<InteropVersionedServerNexus, InteropVersionedServerNexus.ClientProxy>(CreateServerConfig(Type.Uds), null);
        var (v1, _) = CreateClient<InteropVersionedClientNexusV1, InteropVersionedClientNexusV1.ServerProxy>(CreateClientConfig(Type.Uds));
        var (v2, _) = CreateClient<InteropVersionedClientNexusV2, InteropVersionedClientNexusV2.ServerProxy>(CreateClientConfig(Type.Uds));
        await server.StartAsync().Timeout(1);
        await v1.ConnectAsync().Timeout(1);
        await v2.ConnectAsync().Timeout(1);

        await Assert.That(await v1.Proxy.Add(2, 3).AsTask().Timeout(1)).IsEqualTo(5);
        await Assert.That(await v2.Proxy.CreateItem(1, "x").AsTask().Timeout(1)).IsEqualTo(new InteropItem(1, "x"));
    }

    [Test]
    public async Task InjectedFrame_MalformedRemoteErrorDetail_ThrowsPlainException()
    {
        var invocationId = new TaskCompletionSource<ushort>(TaskCreationOptions.RunContinuationsAsynchronously);
        var serverNexus = new TaskCompletionSource<InteropServerNexus>(TaskCreationOptions.RunContinuationsAsynchronously);
        var (client, _) = await ConnectAsync(
            nexus => serverNexus.TrySetResult(nexus),
            configs => configs.Client.InternalOnSend = (_, sent) =>
            {
                if (sent.Length <= 3 || sent[0] != (byte)MessageType.Invocation)
                    return;
                var reader = new MsgPackReader(new ReadOnlyMemory<byte>(sent, 3, sent.Length - 3), NexusSerializerOptions.Untrusted);
                reader.ReadArrayHeader();
                invocationId.TrySetResult(reader.ReadUInt16());
            });

        // The server answers this call only after a minute; the injected frame completes it first.
        var call = client.Proxy.Delay(60_000, 1, CancellationToken.None).AsTask();
        var id = await invocationId.Task.Timeout(1);
        var nexus = await serverNexus.Task.Timeout(1);

        // InvocationResult [id, Exception, detail] whose detail is a string instead of [code, type, message].
        var body = new ArrayBufferWriter<byte>();
        var writer = new MsgPackWriter(body);
        writer.WriteArrayHeader(3);
        writer.Write(id);
        writer.Write((byte)InvocationResultMessage.StateType.Exception);
        writer.Write("not a detail array");
        writer.Flush();
        var frame = new byte[3 + body.WrittenCount];
        frame[0] = (byte)MessageType.InvocationResult;
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(1), (ushort)body.WrittenCount);
        body.WrittenSpan.CopyTo(frame.AsSpan(3));

        var session = (NexusSession<InteropServerNexus, InteropServerNexus.ClientProxy>)nexus.SessionContext.Session;
        await session.InternalSendRawAsync(frame).AsTask().Timeout(1);

        var exception = await Assert.ThrowsExactlyAsync<ProxyRemoteInvocationException>(async () => await call.Timeout(1));
        await Assert.That(exception!.RemoteErrorCode).IsNull();
        await Assert.That(exception.RemoteMessage).IsNull();
        await Assert.That(client.State).IsEqualTo(ConnectionState.Connected);
    }
}
