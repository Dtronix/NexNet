using System.Buffers;
using MessagePack;
using NexNet.Messages;
using NexNet.Pipes;
using static NexNet.Serialization.Tests.TestHelpers;

namespace NexNet.Serialization.Tests;

/// <summary>
/// Golden vectors and malformed-input handling for the hand-written protocol message bodies.
/// </summary>
public class ProtocolMessageTests
{
    private static byte[] Body<T>(T message)
        where T : IMessageBase
    {
        return Write((ref MsgPackWriter w) => message.Serialize(ref w));
    }

    private static T Parse<T>(byte[] body)
        where T : class, IMessageBase, new()
    {
        var message = new T();
        var reader = new MsgPackReader(body);
        message.Deserialize(ref reader);
        Check(reader.End, "Reader did not consume all bytes.");
        return message;
    }

    private static object?[] Reference(byte[] body) => MessagePackSerializer.Deserialize<object?[]>(body);

    [Test]
    public async Task InvocationBodyLength_MatchesTheSerializedBody()
    {
        // Sessions write the frame header from this length before serializing the body, so it must be exact at
        // every MessagePack integer size boundary.
        ushort[] values = [0, 1, 127, 128, 255, 256, 65535];
        InvocationFlags[] flags = [InvocationFlags.None, InvocationFlags.IgnoreReturn, (InvocationFlags)127, (InvocationFlags)128, (InvocationFlags)255];
        byte[][] arguments = [[], [0x91, 0x01], new byte[200], new byte[65_000]];
        arguments[2][0] = 0x91;
        arguments[3][0] = 0x91;

        foreach (var id in values)
        foreach (var methodId in values)
        foreach (var flag in flags)
        foreach (var args in arguments)
        {
            var message = new InvocationMessage { InvocationId = id, MethodId = methodId, Flags = flag, Arguments = args };
            await Assert.That(new InvocationBody(message).Length).IsEqualTo(Body(message).Length).Because($"id {id}, method {methodId}, flags {flag}, arguments {args.Length}");
        }
    }

    [Test]
    public async Task InvocationResultBodyLength_MatchesTheSerializedBody()
    {
        // Sessions write the frame header from this length before serializing the body, so it must be exact at
        // every MessagePack integer size boundary, with and without a result.
        ushort[] ids = [0, 1, 127, 128, 255, 256, 65535];
        byte[]?[] results = [null, [0xC0], [0x01], new byte[200], new byte[65_000]];
        results[3]![0] = 0xC4; // bin 8 header byte; the length test does not parse the value
        results[4]![0] = 0xC5;

        foreach (var id in ids)
        foreach (InvocationResultMessage.StateType state in Enum.GetValues(typeof(InvocationResultMessage.StateType)))
        foreach (var result in results)
        {
            var message = new InvocationResultMessage
            {
                InvocationId = id,
                State = state,
                Result = result == null ? null : new ReadOnlySequence<byte>(result)
            };
            await Assert.That(new InvocationResultBody(message).Length).IsEqualTo(Body(message).Length).Because($"id {id}, state {state}, result {result?.Length.ToString() ?? "none"}");
        }
    }

    [Test]
    public async Task ClientGreetingGoldenVector()
    {
        var message = new ClientGreetingMessage
        {
            Version = "v1",
            ServerNexusHash = 300,
            ClientNexusHash = -5,
            AuthenticationToken = new byte[] { 1, 2 }
        };

        // The two trailing fields are the defaults: maxMessageSize 4 MiB, invocationCredits 32.
        var body = Body(message);
        await Assert.That(Hex(body)).IsEqualTo("96" + "a27631" + "cd012c" + "fb" + "c4020102" + "ce00400000" + "20");

        var parsed = Parse<ClientGreetingMessage>(body);
        await Assert.That(parsed.Version).IsEqualTo("v1");
        await Assert.That(parsed.ServerNexusHash).IsEqualTo(300);
        await Assert.That(parsed.ClientNexusHash).IsEqualTo(-5);
        await Assert.That(parsed.AuthenticationToken.ToArray()).IsEquivalentTo(new byte[] { 1, 2 }, CollectionOrdering.Matching);

        var reference = Reference(body);
        await Assert.That(reference[0]).IsEqualTo("v1");
        await Assert.That(Convert.ToInt32(reference[1])).IsEqualTo(300);
        await Assert.That(Convert.ToInt32(reference[2])).IsEqualTo(-5);
        await Assert.That((byte[]?)reference[3]).IsEquivalentTo(new byte[] { 1, 2 }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task ClientGreetingWithNullVersionAndEmptyToken()
    {
        var body = Body(new ClientGreetingMessage());
        await Assert.That(Hex(body)).IsEqualTo("96c00000c400" + "ce00400000" + "20");
        var parsed = Parse<ClientGreetingMessage>(body);
        await Assert.That(parsed.Version).IsNull();
        await Assert.That(parsed.AuthenticationToken.IsEmpty).IsTrue();
    }

    [Test]
    public async Task ServerGreetingGoldenVector()
    {
        var body = Body(new ServerGreetingMessage { Version = 1, ClientId = 0x1_0000_0000, MaxMessageSize = 65536, InvocationCredits = 128 });
        await Assert.That(Hex(body)).IsEqualTo("94" + "01" + "cf0000000100000000" + "ce00010000" + "cc80");
        var parsed = Parse<ServerGreetingMessage>(body);
        await Assert.That(parsed.ClientId).IsEqualTo(0x1_0000_0000);
        await Assert.That(parsed.MaxMessageSize).IsEqualTo(65536);
        await Assert.That(parsed.InvocationCredits).IsEqualTo(128);
    }

    [Test]
    public async Task InvocationCancellationGoldenVector()
    {
        var body = Body(new InvocationCancellationMessage(1000));
        await Assert.That(Hex(body)).IsEqualTo("91cd03e8");
        await Assert.That(Parse<InvocationCancellationMessage>(body).InvocationId).IsEqualTo(1000);
    }

    [Test]
    public async Task InvocationCancellationIdIsUInt16()
    {
        // The same width as every other invocation ID.
        var body = Body(new InvocationCancellationMessage(ushort.MaxValue));
        await Assert.That(Parse<InvocationCancellationMessage>(body).InvocationId).IsEqualTo(ushort.MaxValue);

        // 65536 and negative IDs are rejected.
        await Assert.That(() => Parse<InvocationCancellationMessage>(new byte[] { 0x91, 0xce, 0x00, 0x01, 0x00, 0x00 })).ThrowsExactly<NexusSerializationException>();
        await Assert.That(() => Parse<InvocationCancellationMessage>(new byte[] { 0x91, 0xff })).ThrowsExactly<NexusSerializationException>();
    }

    [Test]
    public async Task DuplexPipeUpdateStateGoldenVector()
    {
        // [pipeId, state, window]: the Ready acknowledgement grants a 256 KiB window.
        var body = Body(new DuplexPipeUpdateStateMessage { PipeId = 0x0201, State = NexusDuplexPipe.State.Ready, Window = 256 * 1024 });
        await Assert.That(Hex(body)).IsEqualTo("93cd0201" + ((byte)NexusDuplexPipe.State.Ready).ToString("x2") + "ce00040000");
        var parsed = Parse<DuplexPipeUpdateStateMessage>(body);
        await Assert.That(parsed.PipeId).IsEqualTo(0x0201);
        await Assert.That(parsed.State).IsEqualTo(NexusDuplexPipe.State.Ready);
        await Assert.That(parsed.Window).IsEqualTo(256 * 1024);
    }

    [Test]
    public async Task InvocationEmbedsArgumentArray()
    {
        var args = Write((ref MsgPackWriter w) =>
        {
            w.WriteArrayHeader(2);
            w.Write(5);
            w.Write("x");
        });

        var body = Body(new InvocationMessage { InvocationId = 7, MethodId = 300, Flags = InvocationFlags.None, Arguments = args });
        await Assert.That(Hex(body)).IsEqualTo("94" + "07" + "cd012c" + "00" + "9205a178");

        var parsed = Parse<InvocationMessage>(body);
        await Assert.That(parsed.InvocationId).IsEqualTo(7);
        await Assert.That(parsed.MethodId).IsEqualTo(300);
        await Assert.That(parsed.Arguments.ToArray()).IsEquivalentTo(args, CollectionOrdering.Matching);
        parsed.MessageCache = null;

        var reference = Reference(body);
        await Assert.That(((object?[])reference[3]!)[1]).IsEqualTo("x");
    }

    [Test]
    public async Task InvocationWithoutArgumentsSendsEmptyArray()
    {
        var body = Body(new InvocationMessage { InvocationId = 1, MethodId = 2 });
        await Assert.That(Hex(body)).IsEqualTo("9401020090");
    }

    [Test]
    public async Task InvocationResultWithAndWithoutResult()
    {
        var none = Body(new InvocationResultMessage { InvocationId = 3, State = InvocationResultMessage.StateType.CompletedResult });
        await Assert.That(Hex(none)).IsEqualTo("920301");
        var parsedNone = Parse<InvocationResultMessage>(none);
        await Assert.That(parsedNone.Result).IsNull();

        var nilResult = Body(new InvocationResultMessage
        {
            InvocationId = 3,
            State = InvocationResultMessage.StateType.CompletedResult,
            Result = new ReadOnlySequence<byte>(new byte[] { 0xc0 })
        });
        await Assert.That(Hex(nilResult)).IsEqualTo("930301c0");
        var parsedNil = Parse<InvocationResultMessage>(nilResult);
        await Assert.That(parsedNil.Result).IsNotNull();
        await Assert.That(parsedNil.TryGetResult<string>(out var s)).IsTrue();
        await Assert.That(s).IsNull();
    }

    [Test]
    public async Task InvocationResultCanceled()
    {
        var body = Body(new InvocationResultMessage { InvocationId = 3, State = InvocationResultMessage.StateType.Canceled });
        await Assert.That(Hex(body)).IsEqualTo("920304");
        await Assert.That(Parse<InvocationResultMessage>(body).State).IsEqualTo(InvocationResultMessage.StateType.Canceled);
    }

    [Test]
    public async Task InvocationRejectsTruncatedEmbeddedArguments()
    {
        var body = new byte[] { 0x94, 0x01, 0x02, 0x00, 0x92, 0x05 };
        await Assert.That(() => Parse<InvocationMessage>(body)).ThrowsExactly<NexusSerializationException>();
    }

    [Test]
    public async Task WrongArrayCountIsRejected()
    {
        await Assert.That(() => Parse<ServerGreetingMessage>(new byte[] { 0x93, 0x01, 0x02, 0x03 })).ThrowsExactly<NexusSerializationException>();
        await Assert.That(() => Parse<InvocationCancellationMessage>(new byte[] { 0x90 })).ThrowsExactly<NexusSerializationException>();
    }

    [Test]
    public async Task HugeBinaryLengthIsRejected()
    {
        var body = new byte[] { 0x94, 0xc0, 0x00, 0x00, 0xc6, 0x7f, 0xff, 0xff, 0xff };
        await Assert.That(() => Parse<ClientGreetingMessage>(body)).ThrowsExactly<NexusSerializationException>();
    }

    [Test]
    public async Task NeverUsedCodeIsRejected()
    {
        await Assert.That(() => Parse<ServerGreetingMessage>(new byte[] { 0x92, 0xc1, 0x01 })).ThrowsExactly<NexusSerializationException>();
    }
}
