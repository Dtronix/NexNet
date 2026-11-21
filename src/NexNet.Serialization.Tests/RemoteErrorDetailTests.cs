using System.Buffers;
using MessagePack;
using NexNet.Messages;
using static NexNet.Serialization.Tests.TestHelpers;

namespace NexNet.Serialization.Tests;

/// <summary>
/// The remote error detail: the optional third element <c>[code, type, message]</c> of an Exception invocation result.
/// </summary>
public class RemoteErrorDetailTests
{
    private static byte[] Detail(int? code, string? type, string? message)
        => Write((ref MsgPackWriter w) => RemoteErrorDetail.Write(ref w, code, type, message));

    private static bool TryRead(byte[] value, out int? code, out string? type, out string? message)
        => RemoteErrorDetail.TryRead(new ReadOnlySequence<byte>(value), NexusSerializerOptions.Untrusted,
            out code, out type, out message);

    [Test]
    public async Task GoldenVector()
    {
        var bytes = Detail(42, "T", "boom");
        await Assert.That(Convert.ToHexString(bytes).ToLowerInvariant()).IsEqualTo("93" + "2a" + "a154" + "a4626f6f6d");

        var reference = MessagePackSerializer.Deserialize<object?[]>(bytes);
        await Assert.That(Convert.ToInt32(reference[0])).IsEqualTo(42);
        await Assert.That(reference[1]).IsEqualTo("T");
        await Assert.That(reference[2]).IsEqualTo("boom");
    }

    [Test]
    public async Task AllNil()
    {
        var bytes = Detail(null, null, null);
        await Assert.That(Convert.ToHexString(bytes).ToLowerInvariant()).IsEqualTo("93c0c0c0");

        await Assert.That(TryRead(bytes, out var code, out var type, out var message)).IsTrue();
        await Assert.That(code).IsNull();
        await Assert.That(type).IsNull();
        await Assert.That(message).IsNull();
    }

    [Test]
    [Arguments(0)]
    [Arguments(-1)]
    [Arguments(int.MinValue)]
    [Arguments(int.MaxValue)]
    public async Task RoundTrips(int code)
    {
        var bytes = Detail(code, "System.InvalidOperationException", "boom");

        await Assert.That(TryRead(bytes, out var readCode, out var type, out var message)).IsTrue();
        await Assert.That(readCode).IsEqualTo(code);
        await Assert.That(type).IsEqualTo("System.InvalidOperationException");
        await Assert.That(message).IsEqualTo("boom");
    }

    [Test]
    public async Task TruncatesTypeAndMessageOnWrite()
    {
        var bytes = Detail(null, new string('t', 300), new string('m', 3000));

        var reference = MessagePackSerializer.Deserialize<object?[]>(bytes);
        await Assert.That(reference[1]).IsEqualTo(new string('t', 256));
        await Assert.That(reference[2]).IsEqualTo(new string('m', 2048));

        await Assert.That(TryRead(bytes, out _, out var type, out var message)).IsTrue();
        await Assert.That(type).IsEqualTo(new string('t', 256));
        await Assert.That(message).IsEqualTo(new string('m', 2048));
    }

    [Test]
    public async Task TruncationDoesNotSplitASurrogatePair()
    {
        // 2047 chars then a surrogate pair: the pair would straddle the 2048 limit.
        var message = new string('m', 2047) + "\U0001F600" + "tail";
        var bytes = Detail(null, null, message);

        await Assert.That(TryRead(bytes, out _, out _, out var read)).IsTrue();
        await Assert.That(read).IsEqualTo(new string('m', 2047));
    }

    [Test]
    public async Task KeepsStringsAtTheLimit()
    {
        var bytes = Detail(1, new string('t', 256), new string('m', 2048));

        await Assert.That(TryRead(bytes, out _, out var type, out var message)).IsTrue();
        await Assert.That(type).Length().IsEqualTo(256);
        await Assert.That(message).Length().IsEqualTo(2048);
    }

    // Well-formed MessagePack values that are not a valid detail, and oversized strings.
    [Test]
    [Arguments("05")]
    [Arguments("c0")]
    [Arguments("90")]
    [Arguments("9201a174")]
    [Arguments("9401a174a16da178")]
    [Arguments("93a131a174a16d")]
    [Arguments("930102a16d")]
    [Arguments("9301a17491a16d")]
    [Arguments("93cf0000000100000000c0c0")]
    [Arguments("8101a174")]
    public async Task TryRead_RejectsMalformedWithoutThrowing(string hex)
    {
        var value = Convert.FromHexString(hex);

        bool read = true;
        await Assert.That(() => read = TryRead(value, out _, out _, out _)).ThrowsNothing();
        await Assert.That(read).IsFalse();
    }

    [Test]
    [Arguments(257, 1)]
    [Arguments(1, 2049)]
    public async Task TryRead_RejectsOversizedStrings(int typeLength, int messageLength)
    {
        var value = MessagePackSerializer.Serialize<object?[]>(
            [1, new string('t', typeLength), new string('m', messageLength)]);

        bool read = true;
        await Assert.That(() => read = TryRead(value, out _, out _, out _)).ThrowsNothing();
        await Assert.That(read).IsFalse();
    }

    [Test]
    public async Task ExceptionResultWithDetail_IsAThirdElementAnExistingPeerParses()
    {
        var detail = Detail(42, "T", "boom");
        var message = new InvocationResultMessage
        {
            InvocationId = 3,
            State = InvocationResultMessage.StateType.Exception,
            Result = new ReadOnlySequence<byte>(detail)
        };

        var body = Write((ref MsgPackWriter w) => message.Serialize(ref w));
        await Assert.That(Convert.ToHexString(body).ToLowerInvariant()).IsEqualTo("930302" + "932aa154a4626f6f6d");
        await Assert.That(new InvocationResultBody(message).Length).IsEqualTo(body.Length);

        // Parsed as a peer without this feature parses it: three elements, the detail kept as the embedded result.
        var parsed = new InvocationResultMessage();
        var reader = new MsgPackReader(new ReadOnlyMemory<byte>(body));
        parsed.Deserialize(ref reader);
        await Assert.That(reader.End).IsTrue();
        await Assert.That(parsed.State).IsEqualTo(InvocationResultMessage.StateType.Exception);
        await Assert.That(parsed.Result).IsNotNull();
        await Assert.That(TryRead(parsed.Result!.Value.ToArray(), out var code, out var type, out var text)).IsTrue();
        await Assert.That(code).IsEqualTo(42);
        await Assert.That(type).IsEqualTo("T");
        await Assert.That(text).IsEqualTo("boom");
    }
}
