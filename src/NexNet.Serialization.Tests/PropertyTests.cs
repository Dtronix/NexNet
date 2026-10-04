using CsCheck;
using MessagePack;

namespace NexNet.Serialization.Tests;

/// <summary>
/// Property-based round trips and cross-checks against MessagePack-CSharp.
/// </summary>
[TestFixture]
public class PropertyTests
{
    [Test]
    public void Int64RoundTripsAndMatchesReference()
    {
        Gen.Long.Sample(v =>
        {
            var bytes = TestHelpers.Serialize(v);
            return TestHelpers.Deserialize<long>(bytes) == v
                   && bytes.AsSpan().SequenceEqual(MessagePackSerializer.Serialize(v));
        });
    }

    [Test]
    public void UInt64RoundTripsAndMatchesReference()
    {
        Gen.ULong.Sample(v =>
        {
            var bytes = TestHelpers.Serialize(v);
            return TestHelpers.Deserialize<ulong>(bytes) == v
                   && bytes.AsSpan().SequenceEqual(MessagePackSerializer.Serialize(v));
        });
    }

    [Test]
    public void StringsRoundTripAndMatchReference()
    {
        Gen.String.Sample(s =>
        {
            var bytes = TestHelpers.Serialize(s);
            var reference = MessagePackSerializer.Serialize(s);
            return TestHelpers.Deserialize<string>(bytes, NexusSerializerOptions.Trusted) == MessagePackSerializer.Deserialize<string>(reference)
                   && bytes.AsSpan().SequenceEqual(reference);
        });
    }

    [Test]
    public void DoubleArraysRoundTrip()
    {
        Gen.Double.Array.Sample(values =>
        {
            var result = TestHelpers.Deserialize<double[]>(TestHelpers.Serialize(values))!;
            return result.AsSpan().SequenceEqual(values);
        });
    }

    [Test]
    public void ReferenceObjectsAreSkippable()
    {
        Gen.Int.Sample(seed =>
        {
            var value = ReaderTests.RandomObject(new Random(seed), 0);
            var bytes = MessagePackSerializer.Serialize<object?>(value);
            var reader = new MsgPackReader(bytes);
            return reader.TrySkip() && reader.End;
        }, iter: 2000);
    }
}
