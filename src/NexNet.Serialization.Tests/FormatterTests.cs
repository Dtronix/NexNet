using System.Buffers;
using System.Numerics;
using MessagePack;
using NexNet.Serialization.Formatters;
using static NexNet.Serialization.Tests.TestHelpers;

namespace NexNet.Serialization.Tests;

public class FormatterTests
{
    private static async Task RoundTrip<T>(T value)
    {
        var bytes = TestHelpers.Serialize(value);
        var result = TestHelpers.Deserialize<T>(bytes);
        if (value is System.Collections.IEnumerable expected and not string)
        {
            // Collections compare element by element, in order.
            await Assert.That(((System.Collections.IEnumerable?)result)?.Cast<object?>())
                .IsEquivalentTo(expected.Cast<object?>(), CollectionOrdering.Matching);
        }
        else
        {
            await Assert.That(result).IsEqualTo(value);
        }
    }

    [Test]
    public async Task ScalarsRoundTrip()
    {
        await RoundTrip(true);
        await RoundTrip((byte)200);
        await RoundTrip((sbyte)-100);
        await RoundTrip((short)-30000);
        await RoundTrip((ushort)60000);
        await RoundTrip(int.MinValue);
        await RoundTrip(uint.MaxValue);
        await RoundTrip(long.MinValue);
        await RoundTrip(ulong.MaxValue);
        await RoundTrip(3.5f);
        await RoundTrip(-2.25d);
        await RoundTrip((Half)1.5);
        await RoundTrip('字');
        await RoundTrip("text");
        await RoundTrip<string?>(null);
        await RoundTrip(new DateTime(2026, 10, 3, 12, 30, 15, DateTimeKind.Utc));
        await RoundTrip(new DateTime(2026, 10, 3, 12, 30, 15, DateTimeKind.Unspecified));
        await RoundTrip(new DateTimeOffset(2026, 10, 3, 12, 30, 15, TimeSpan.FromHours(-5)));
        await RoundTrip(TimeSpan.FromMilliseconds(123456789));
        await RoundTrip(new DateOnly(2026, 10, 3));
        await RoundTrip(new TimeOnly(23, 59, 59, 999));
        await RoundTrip(Guid.NewGuid());
        await RoundTrip(123456789.987654321m);
        await RoundTrip(-0.0000001m);
        await RoundTrip(BigInteger.Parse("-123456789012345678901234567890"));
        await RoundTrip(new Uri("https://example.com/path?q=1"));
        await RoundTrip(new Version(1, 2, 3, 4));
        await RoundTrip<int?>(5);
        await RoundTrip<int?>(null);
        await RoundTrip<Guid?>(Guid.NewGuid());
    }

    [Test]
    public async Task DateTimePreservesKind()
    {
        var local = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Local);
        var result = TestHelpers.Deserialize<DateTime>(TestHelpers.Serialize(local));
        await Assert.That(result.Kind).IsEqualTo(DateTimeKind.Local);
        await Assert.That(result).IsEqualTo(local);
    }

    [Test]
    public async Task DateTimeRejectsOutOfRangeTicks()
    {
        // Utc kind bit with ticks above DateTime.MaxValue.Ticks.
        var invalid = (long)(0x4000000000000000UL | (ulong)(DateTime.MaxValue.Ticks + 1));
        await Assert.That(() => TestHelpers.Deserialize<DateTime>(TestHelpers.Serialize(invalid))).ThrowsExactly<NexusSerializationException>();

        var maxUtc = DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc);
        var result = TestHelpers.Deserialize<DateTime>(TestHelpers.Serialize(maxUtc));
        await Assert.That(result).IsEqualTo(maxUtc);
        await Assert.That(result.Kind).IsEqualTo(DateTimeKind.Utc);
    }

    [Test]
    public async Task GuidIsBin16BigEndian()
    {
        var guid = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
        await Assert.That(Hex(TestHelpers.Serialize(guid))).IsEqualTo("c41000112233445566778899aabbccddeeff");
    }

    [Test]
    public async Task DecimalIsBin16()
    {
        var bytes = TestHelpers.Serialize(1m);
        await Assert.That(Hex(bytes)).IsEqualTo("c410" + "01000000" + "00000000" + "00000000" + "00000000");
    }

    [Test]
    public async Task BinaryTypesRoundTrip()
    {
        var data = new byte[] { 1, 2, 3, 255 };
        await Assert.That(TestHelpers.Deserialize<byte[]>(TestHelpers.Serialize(data))).IsEquivalentTo(data, CollectionOrdering.Matching);
        await Assert.That(TestHelpers.Deserialize<Memory<byte>>(TestHelpers.Serialize(new Memory<byte>(data))).ToArray()).IsEquivalentTo(data, CollectionOrdering.Matching);
        await Assert.That(TestHelpers.Deserialize<ReadOnlyMemory<byte>>(TestHelpers.Serialize(new ReadOnlyMemory<byte>(data))).ToArray()).IsEquivalentTo(data, CollectionOrdering.Matching);
        await Assert.That(TestHelpers.Deserialize<ReadOnlySequence<byte>>(TestHelpers.Serialize(new ReadOnlySequence<byte>(data))).ToArray()).IsEquivalentTo(data, CollectionOrdering.Matching);
        await Assert.That(TestHelpers.Deserialize<byte[]>(TestHelpers.Serialize<byte[]?>(null))).IsNull();
    }

    [Test]
    public async Task CollectionsRoundTrip()
    {
        await RoundTrip(new[] { "a", "b", null });
        await RoundTrip(new List<string?> { "x", null, "z" });
        await Assert.That(TestHelpers.Deserialize<IList<string>>(TestHelpers.Serialize<IList<string>>(new List<string> { "1", "2" }))).IsEquivalentTo(new[] { "1", "2" }, CollectionOrdering.Matching);
        await Assert.That(TestHelpers.Deserialize<IEnumerable<string>>(TestHelpers.Serialize<IEnumerable<string>>(new[] { "q" }.Select(x => x)))).IsEquivalentTo(new[] { "q" }, CollectionOrdering.Matching);
        await RoundTrip(new Dictionary<string, int> { ["a"] = 1, ["b"] = 2 });
        await RoundTrip(new Dictionary<int, string> { [1] = "a", [-2] = "b" });
        await RoundTrip(new List<byte> { 1, 2, 3 });

        NexusFormatterRegistry.Register(new HashSetFormatter<int>());
        await RoundTrip(new HashSet<int> { 1, 5, 9 });
        NexusFormatterRegistry.Register(new QueueFormatter<int>());
        var queue = TestHelpers.Deserialize<Queue<int>>(TestHelpers.Serialize(new Queue<int>(new[] { 1, 2, 3 })));
        await Assert.That(queue).IsEquivalentTo(new[] { 1, 2, 3 }, CollectionOrdering.Matching);
        NexusFormatterRegistry.Register(new StackFormatter<int>());
        var stack = new Stack<int>(new[] { 1, 2, 3 });
        await Assert.That(TestHelpers.Deserialize<Stack<int>>(TestHelpers.Serialize(stack))).IsEquivalentTo(stack, CollectionOrdering.Matching);
        NexusFormatterRegistry.Register(new KeyValuePairFormatter<string, int>());
        await RoundTrip(new KeyValuePair<string, int>("k", 3));
        NexusFormatterRegistry.Register(new ValueTupleFormatter<int, string, double>());
        await RoundTrip((1, "two", 3.0));
    }

    [Test]
    public async Task DeserializedDictionaryUsesRandomizedComparerWhenUntrusted()
    {
        var bytes = TestHelpers.Serialize(new Dictionary<int, string> { [1] = "a" });
        var untrusted = TestHelpers.Deserialize<Dictionary<int, string>>(bytes, NexusSerializerOptions.Untrusted)!;
        var trusted = TestHelpers.Deserialize<Dictionary<int, string>>(bytes, NexusSerializerOptions.Trusted)!;
        await Assert.That(untrusted.Comparer).IsNotSameReferenceAs(EqualityComparer<int>.Default);
        await Assert.That(trusted.Comparer).IsSameReferenceAs(EqualityComparer<int>.Default);
        await Assert.That(untrusted[1]).IsEqualTo("a");
    }

    [Test]
    public async Task EnumsUseUnderlyingInteger()
    {
        var formatter = EnumFormatter<DayOfWeek>.Instance;
        var bytes = Write((ref MsgPackWriter w) => formatter.Serialize(ref w, DayOfWeek.Friday));
        await Assert.That(Hex(bytes)).IsEqualTo("05");
        var reader = new MsgPackReader(bytes);
        DayOfWeek day = default;
        formatter.Deserialize(ref reader, ref day);
        await Assert.That(day).IsEqualTo(DayOfWeek.Friday);
    }

    [Test]
    public async Task MissingFormatterThrowsDescriptiveException()
    {
        var ex = await Assert.That(() => NexusFormatterRegistry.Get<FormatterTests>()).ThrowsExactly<NexusSerializationException>();
        await Assert.That(ex!.Message).Contains("NexusObject");
    }

    // ------------------------------------------------------------------ Cross-checks with MessagePack-CSharp

    [Test]
    public async Task ReferenceReadsNexNetOutput()
    {
        await Assert.That(MessagePackSerializer.Deserialize<string[]>(TestHelpers.Serialize(new[] { "a", "bc" }))).IsEquivalentTo(new[] { "a", "bc" }, CollectionOrdering.Matching);
        await Assert.That(MessagePackSerializer.Deserialize<Dictionary<string, int>>(TestHelpers.Serialize(new Dictionary<string, int> { ["x"] = 300 }))).IsEquivalentTo(new Dictionary<string, int> { ["x"] = 300 }, CollectionOrdering.Matching);
        await Assert.That(MessagePackSerializer.Deserialize<long>(TestHelpers.Serialize(long.MinValue))).IsEqualTo(long.MinValue);
        await Assert.That(MessagePackSerializer.Deserialize<byte[]>(TestHelpers.Serialize(new byte[] { 9, 8 }))).IsEquivalentTo(new byte[] { 9, 8 }, CollectionOrdering.Matching);
        await Assert.That(MessagePackSerializer.Deserialize<TimeSpan>(TestHelpers.Serialize(TimeSpan.FromTicks(12345)))).IsEqualTo(TimeSpan.FromTicks(12345));
    }

    [Test]
    public async Task NexNetReadsReferenceOutput()
    {
        await Assert.That(TestHelpers.Deserialize<string[]>(MessagePackSerializer.Serialize(new[] { "a", null, "c" }))).IsEquivalentTo(new[] { "a", null, "c" }, CollectionOrdering.Matching);
        await Assert.That(TestHelpers.Deserialize<Dictionary<int, string>>(MessagePackSerializer.Serialize(new Dictionary<int, string> { [7] = "seven" }))).IsEquivalentTo(new Dictionary<int, string> { [7] = "seven" }, CollectionOrdering.Matching);
        await Assert.That(TestHelpers.Deserialize<ulong>(MessagePackSerializer.Serialize(ulong.MaxValue))).IsEqualTo(ulong.MaxValue);
        await Assert.That(TestHelpers.Deserialize<bool[]>(MessagePackSerializer.Serialize(new[] { true, false }))).IsEquivalentTo(new[] { true, false }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task PrimitiveArrayExtIsReadableByReferenceAsExt()
    {
        var bytes = TestHelpers.Serialize(new[] { 1, 2, -3 });
        var reader = new MessagePackReader(bytes);
        var ext = reader.ReadExtensionFormat();
        await Assert.That(ext.Header.TypeCode).IsEqualTo(NexusExtType.PrimitiveArray);
        var data = ext.Data.ToArray();
        await Assert.That(data[0]).IsEqualTo((byte)PrimitiveKind.Int32);
        await Assert.That(Hex(data[1..])).IsEqualTo("01000000" + "02000000" + "fdffffff");
    }
}
