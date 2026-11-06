using System.Buffers;
using MessagePack;
using NexNet.Messages;
using static NexNet.Serialization.Tests.TestHelpers;

namespace NexNet.Serialization.Tests;

/// <summary>
/// The remote error detail: the optional third element <c>[code, type, message]</c> of an Exception invocation result.
/// </summary>
[TestFixture]
public class RemoteErrorDetailTests
{
    private static byte[] Detail(int? code, string? type, string? message)
        => Write((ref MsgPackWriter w) => RemoteErrorDetail.Write(ref w, code, type, message));

    private static bool TryRead(byte[] value, out int? code, out string? type, out string? message)
        => RemoteErrorDetail.TryRead(new ReadOnlySequence<byte>(value), NexusSerializerOptions.Untrusted,
            out code, out type, out message);

    [Test]
    public void GoldenVector()
    {
        var bytes = Detail(42, "T", "boom");
        Assert.That(Convert.ToHexString(bytes).ToLowerInvariant(), Is.EqualTo("93" + "2a" + "a154" + "a4626f6f6d"));

        var reference = MessagePackSerializer.Deserialize<object?[]>(bytes);
        Assert.That(Convert.ToInt32(reference[0]), Is.EqualTo(42));
        Assert.That(reference[1], Is.EqualTo("T"));
        Assert.That(reference[2], Is.EqualTo("boom"));
    }

    [Test]
    public void AllNil()
    {
        var bytes = Detail(null, null, null);
        Assert.That(Convert.ToHexString(bytes).ToLowerInvariant(), Is.EqualTo("93c0c0c0"));

        Assert.That(TryRead(bytes, out var code, out var type, out var message), Is.True);
        Assert.That(code, Is.Null);
        Assert.That(type, Is.Null);
        Assert.That(message, Is.Null);
    }

    [TestCase(0)]
    [TestCase(-1)]
    [TestCase(int.MinValue)]
    [TestCase(int.MaxValue)]
    public void RoundTrips(int code)
    {
        var bytes = Detail(code, "System.InvalidOperationException", "boom");

        Assert.That(TryRead(bytes, out var readCode, out var type, out var message), Is.True);
        Assert.That(readCode, Is.EqualTo(code));
        Assert.That(type, Is.EqualTo("System.InvalidOperationException"));
        Assert.That(message, Is.EqualTo("boom"));
    }

    [Test]
    public void TruncatesTypeAndMessageOnWrite()
    {
        var bytes = Detail(null, new string('t', 300), new string('m', 3000));

        var reference = MessagePackSerializer.Deserialize<object?[]>(bytes);
        Assert.That(reference[1], Is.EqualTo(new string('t', 256)));
        Assert.That(reference[2], Is.EqualTo(new string('m', 2048)));

        Assert.That(TryRead(bytes, out _, out var type, out var message), Is.True);
        Assert.That(type, Is.EqualTo(new string('t', 256)));
        Assert.That(message, Is.EqualTo(new string('m', 2048)));
    }

    [Test]
    public void TruncationDoesNotSplitASurrogatePair()
    {
        // 2047 chars then a surrogate pair: the pair would straddle the 2048 limit.
        var message = new string('m', 2047) + "\U0001F600" + "tail";
        var bytes = Detail(null, null, message);

        Assert.That(TryRead(bytes, out _, out _, out var read), Is.True);
        Assert.That(read, Is.EqualTo(new string('m', 2047)));
    }

    [Test]
    public void KeepsStringsAtTheLimit()
    {
        var bytes = Detail(1, new string('t', 256), new string('m', 2048));

        Assert.That(TryRead(bytes, out _, out var type, out var message), Is.True);
        Assert.That(type, Has.Length.EqualTo(256));
        Assert.That(message, Has.Length.EqualTo(2048));
    }

    // Well-formed MessagePack values that are not a valid detail, and oversized strings.
    [TestCase("05")]
    [TestCase("c0")]
    [TestCase("90")]
    [TestCase("9201a174")]
    [TestCase("9401a174a16da178")]
    [TestCase("93a131a174a16d")]
    [TestCase("930102a16d")]
    [TestCase("9301a17491a16d")]
    [TestCase("93cf0000000100000000c0c0")]
    [TestCase("8101a174")]
    public void TryRead_RejectsMalformedWithoutThrowing(string hex)
    {
        var value = Convert.FromHexString(hex);

        bool read = true;
        Assert.DoesNotThrow(() => read = TryRead(value, out _, out _, out _));
        Assert.That(read, Is.False);
    }

    [TestCase(257, 1)]
    [TestCase(1, 2049)]
    public void TryRead_RejectsOversizedStrings(int typeLength, int messageLength)
    {
        var value = MessagePackSerializer.Serialize<object?[]>(
            [1, new string('t', typeLength), new string('m', messageLength)]);

        bool read = true;
        Assert.DoesNotThrow(() => read = TryRead(value, out _, out _, out _));
        Assert.That(read, Is.False);
    }

    [Test]
    public void ExceptionResultWithDetail_IsAThirdElementAnExistingPeerParses()
    {
        var detail = Detail(42, "T", "boom");
        var message = new InvocationResultMessage
        {
            InvocationId = 3,
            State = InvocationResultMessage.StateType.Exception,
            Result = new ReadOnlySequence<byte>(detail)
        };

        var body = Write((ref MsgPackWriter w) => message.Serialize(ref w));
        Assert.That(Convert.ToHexString(body).ToLowerInvariant(), Is.EqualTo("930302" + "932aa154a4626f6f6d"));
        Assert.That(new InvocationResultBody(message).Length, Is.EqualTo(body.Length));

        // Parsed as a peer without this feature parses it: three elements, the detail kept as the embedded result.
        var parsed = new InvocationResultMessage();
        var reader = new MsgPackReader(new ReadOnlyMemory<byte>(body));
        parsed.Deserialize(ref reader);
        Assert.That(reader.End, Is.True);
        Assert.That(parsed.State, Is.EqualTo(InvocationResultMessage.StateType.Exception));
        Assert.That(parsed.Result, Is.Not.Null);
        Assert.That(TryRead(parsed.Result!.Value.ToArray(), out var code, out var type, out var text), Is.True);
        Assert.That(code, Is.EqualTo(42));
        Assert.That(type, Is.EqualTo("T"));
        Assert.That(text, Is.EqualTo("boom"));
    }
}
