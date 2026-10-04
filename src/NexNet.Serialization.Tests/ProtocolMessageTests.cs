using System.Buffers;
using MessagePack;
using NexNet.Messages;
using NexNet.Pipes;
using static NexNet.Serialization.Tests.TestHelpers;

namespace NexNet.Serialization.Tests;

/// <summary>
/// Golden vectors and malformed-input handling for the hand-written protocol message bodies.
/// </summary>
[TestFixture]
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
        Assert.That(reader.End, Is.True);
        return message;
    }

    private static object?[] Reference(byte[] body) => MessagePackSerializer.Deserialize<object?[]>(body);

    [Test]
    public void ClientGreetingGoldenVector()
    {
        var message = new ClientGreetingMessage
        {
            Version = "v1",
            ServerNexusHash = 300,
            ClientNexusHash = -5,
            AuthenticationToken = new byte[] { 1, 2 }
        };

        var body = Body(message);
        Assert.That(Hex(body), Is.EqualTo("94" + "a27631" + "cd012c" + "fb" + "c4020102"));

        var parsed = Parse<ClientGreetingMessage>(body);
        Assert.That(parsed.Version, Is.EqualTo("v1"));
        Assert.That(parsed.ServerNexusHash, Is.EqualTo(300));
        Assert.That(parsed.ClientNexusHash, Is.EqualTo(-5));
        Assert.That(parsed.AuthenticationToken.ToArray(), Is.EqualTo(new byte[] { 1, 2 }));

        var reference = Reference(body);
        Assert.That(reference[0], Is.EqualTo("v1"));
        Assert.That(Convert.ToInt32(reference[1]), Is.EqualTo(300));
        Assert.That(Convert.ToInt32(reference[2]), Is.EqualTo(-5));
        Assert.That(reference[3], Is.EqualTo(new byte[] { 1, 2 }));
    }

    [Test]
    public void ClientGreetingWithNullVersionAndEmptyToken()
    {
        var body = Body(new ClientGreetingMessage());
        Assert.That(Hex(body), Is.EqualTo("94c00000c400"));
        var parsed = Parse<ClientGreetingMessage>(body);
        Assert.That(parsed.Version, Is.Null);
        Assert.That(parsed.AuthenticationToken.IsEmpty, Is.True);
    }

    [Test]
    public void ServerGreetingGoldenVector()
    {
        var body = Body(new ServerGreetingMessage { Version = 1, ClientId = 0x1_0000_0000 });
        Assert.That(Hex(body), Is.EqualTo("92" + "01" + "cf0000000100000000"));
        var parsed = Parse<ServerGreetingMessage>(body);
        Assert.That(parsed.ClientId, Is.EqualTo(0x1_0000_0000));
    }

    [Test]
    public void InvocationCancellationGoldenVector()
    {
        var body = Body(new InvocationCancellationMessage(1000));
        Assert.That(Hex(body), Is.EqualTo("91cd03e8"));
        Assert.That(Parse<InvocationCancellationMessage>(body).InvocationId, Is.EqualTo(1000));
    }

    [Test]
    public void DuplexPipeUpdateStateGoldenVector()
    {
        var body = Body(new DuplexPipeUpdateStateMessage { PipeId = 0x0201, State = NexusDuplexPipe.State.Ready });
        Assert.That(Hex(body), Is.EqualTo("92cd0201" + ((byte)NexusDuplexPipe.State.Ready).ToString("x2")));
        var parsed = Parse<DuplexPipeUpdateStateMessage>(body);
        Assert.That(parsed.PipeId, Is.EqualTo(0x0201));
        Assert.That(parsed.State, Is.EqualTo(NexusDuplexPipe.State.Ready));
    }

#if !NEXNET_MEMORYPACK
    [Test]
    public void InvocationEmbedsArgumentArray()
    {
        var args = Write((ref MsgPackWriter w) =>
        {
            w.WriteArrayHeader(2);
            w.Write(5);
            w.Write("x");
        });

        var body = Body(new InvocationMessage { InvocationId = 7, MethodId = 300, Flags = InvocationFlags.None, Arguments = args });
        Assert.That(Hex(body), Is.EqualTo("94" + "07" + "cd012c" + "00" + "9205a178"));

        var parsed = Parse<InvocationMessage>(body);
        Assert.That(parsed.InvocationId, Is.EqualTo(7));
        Assert.That(parsed.MethodId, Is.EqualTo(300));
        Assert.That(parsed.Arguments.ToArray(), Is.EqualTo(args));
        parsed.MessageCache = null;

        var reference = Reference(body);
        Assert.That(((object?[])reference[3]!)[1], Is.EqualTo("x"));
    }

    [Test]
    public void InvocationWithoutArgumentsSendsEmptyArray()
    {
        var body = Body(new InvocationMessage { InvocationId = 1, MethodId = 2 });
        Assert.That(Hex(body), Is.EqualTo("9401020090"));
    }

    [Test]
    public void InvocationResultWithAndWithoutResult()
    {
        var none = Body(new InvocationResultMessage { InvocationId = 3, State = InvocationResultMessage.StateType.CompletedResult });
        Assert.That(Hex(none), Is.EqualTo("920301"));
        var parsedNone = Parse<InvocationResultMessage>(none);
        Assert.That(parsedNone.Result, Is.Null);

        var nilResult = Body(new InvocationResultMessage
        {
            InvocationId = 3,
            State = InvocationResultMessage.StateType.CompletedResult,
            Result = new ReadOnlySequence<byte>(new byte[] { 0xc0 })
        });
        Assert.That(Hex(nilResult), Is.EqualTo("930301c0"));
        var parsedNil = Parse<InvocationResultMessage>(nilResult);
        Assert.That(parsedNil.Result, Is.Not.Null);
        Assert.That(parsedNil.TryGetResult<string>(out var s), Is.True);
        Assert.That(s, Is.Null);
    }

    [Test]
    public void InvocationRejectsTruncatedEmbeddedArguments()
    {
        var body = new byte[] { 0x94, 0x01, 0x02, 0x00, 0x92, 0x05 };
        Assert.Throws<NexusSerializationException>(() => Parse<InvocationMessage>(body));
    }
#endif

    [Test]
    public void WrongArrayCountIsRejected()
    {
        Assert.Throws<NexusSerializationException>(() => Parse<ServerGreetingMessage>(new byte[] { 0x93, 0x01, 0x02, 0x03 }));
        Assert.Throws<NexusSerializationException>(() => Parse<InvocationCancellationMessage>(new byte[] { 0x90 }));
    }

    [Test]
    public void HugeBinaryLengthIsRejected()
    {
        var body = new byte[] { 0x94, 0xc0, 0x00, 0x00, 0xc6, 0x7f, 0xff, 0xff, 0xff };
        Assert.Throws<NexusSerializationException>(() => Parse<ClientGreetingMessage>(body));
    }

    [Test]
    public void NeverUsedCodeIsRejected()
    {
        Assert.Throws<NexusSerializationException>(() => Parse<ServerGreetingMessage>(new byte[] { 0x92, 0xc1, 0x01 }));
    }
}
