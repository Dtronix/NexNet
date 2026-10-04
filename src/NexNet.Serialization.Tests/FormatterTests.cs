using System.Buffers;
using System.Numerics;
using MessagePack;
using NexNet.Serialization.Formatters;
using static NexNet.Serialization.Tests.TestHelpers;

namespace NexNet.Serialization.Tests;

[TestFixture]
public class FormatterTests
{
    private static void RoundTrip<T>(T value)
    {
        var bytes = TestHelpers.Serialize(value);
        var result = TestHelpers.Deserialize<T>(bytes);
        Assert.That(result, Is.EqualTo(value));
    }

    [Test]
    public void ScalarsRoundTrip()
    {
        RoundTrip(true);
        RoundTrip((byte)200);
        RoundTrip((sbyte)-100);
        RoundTrip((short)-30000);
        RoundTrip((ushort)60000);
        RoundTrip(int.MinValue);
        RoundTrip(uint.MaxValue);
        RoundTrip(long.MinValue);
        RoundTrip(ulong.MaxValue);
        RoundTrip(3.5f);
        RoundTrip(-2.25d);
        RoundTrip((Half)1.5);
        RoundTrip('字');
        RoundTrip("text");
        RoundTrip<string?>(null);
        RoundTrip(new DateTime(2026, 10, 3, 12, 30, 15, DateTimeKind.Utc));
        RoundTrip(new DateTime(2026, 10, 3, 12, 30, 15, DateTimeKind.Unspecified));
        RoundTrip(new DateTimeOffset(2026, 10, 3, 12, 30, 15, TimeSpan.FromHours(-5)));
        RoundTrip(TimeSpan.FromMilliseconds(123456789));
        RoundTrip(new DateOnly(2026, 10, 3));
        RoundTrip(new TimeOnly(23, 59, 59, 999));
        RoundTrip(Guid.NewGuid());
        RoundTrip(123456789.987654321m);
        RoundTrip(-0.0000001m);
        RoundTrip(BigInteger.Parse("-123456789012345678901234567890"));
        RoundTrip(new Uri("https://example.com/path?q=1"));
        RoundTrip(new Version(1, 2, 3, 4));
        RoundTrip<int?>(5);
        RoundTrip<int?>(null);
        RoundTrip<Guid?>(Guid.NewGuid());
    }

    [Test]
    public void DateTimePreservesKind()
    {
        var local = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Local);
        var result = TestHelpers.Deserialize<DateTime>(TestHelpers.Serialize(local));
        Assert.That(result.Kind, Is.EqualTo(DateTimeKind.Local));
        Assert.That(result, Is.EqualTo(local));
    }

    [Test]
    public void DateTimeRejectsOutOfRangeTicks()
    {
        // Utc kind bit with ticks above DateTime.MaxValue.Ticks.
        var invalid = (long)(0x4000000000000000UL | (ulong)(DateTime.MaxValue.Ticks + 1));
        Assert.Throws<NexusSerializationException>(() => TestHelpers.Deserialize<DateTime>(TestHelpers.Serialize(invalid)));

        var maxUtc = DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc);
        var result = TestHelpers.Deserialize<DateTime>(TestHelpers.Serialize(maxUtc));
        Assert.That(result, Is.EqualTo(maxUtc));
        Assert.That(result.Kind, Is.EqualTo(DateTimeKind.Utc));
    }

    [Test]
    public void GuidIsBin16BigEndian()
    {
        var guid = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
        Assert.That(Hex(TestHelpers.Serialize(guid)), Is.EqualTo("c41000112233445566778899aabbccddeeff"));
    }

    [Test]
    public void DecimalIsBin16()
    {
        var bytes = TestHelpers.Serialize(1m);
        Assert.That(Hex(bytes), Is.EqualTo("c410" + "01000000" + "00000000" + "00000000" + "00000000"));
    }

    [Test]
    public void BinaryTypesRoundTrip()
    {
        var data = new byte[] { 1, 2, 3, 255 };
        Assert.That(TestHelpers.Deserialize<byte[]>(TestHelpers.Serialize(data)), Is.EqualTo(data));
        Assert.That(TestHelpers.Deserialize<Memory<byte>>(TestHelpers.Serialize(new Memory<byte>(data))).ToArray(), Is.EqualTo(data));
        Assert.That(TestHelpers.Deserialize<ReadOnlyMemory<byte>>(TestHelpers.Serialize(new ReadOnlyMemory<byte>(data))).ToArray(), Is.EqualTo(data));
        Assert.That(TestHelpers.Deserialize<ReadOnlySequence<byte>>(TestHelpers.Serialize(new ReadOnlySequence<byte>(data))).ToArray(), Is.EqualTo(data));
        Assert.That(TestHelpers.Deserialize<byte[]>(TestHelpers.Serialize<byte[]?>(null)), Is.Null);
    }

    [Test]
    public void CollectionsRoundTrip()
    {
        RoundTrip(new[] { "a", "b", null });
        RoundTrip(new List<string?> { "x", null, "z" });
        Assert.That(TestHelpers.Deserialize<IList<string>>(TestHelpers.Serialize<IList<string>>(new List<string> { "1", "2" })), Is.EqualTo(new[] { "1", "2" }));
        Assert.That(TestHelpers.Deserialize<IEnumerable<string>>(TestHelpers.Serialize<IEnumerable<string>>(new[] { "q" }.Select(x => x))), Is.EqualTo(new[] { "q" }));
        RoundTrip(new Dictionary<string, int> { ["a"] = 1, ["b"] = 2 });
        RoundTrip(new Dictionary<int, string> { [1] = "a", [-2] = "b" });
        RoundTrip(new List<byte> { 1, 2, 3 });

        NexusFormatterRegistry.Register(new HashSetFormatter<int>());
        RoundTrip(new HashSet<int> { 1, 5, 9 });
        NexusFormatterRegistry.Register(new QueueFormatter<int>());
        var queue = TestHelpers.Deserialize<Queue<int>>(TestHelpers.Serialize(new Queue<int>(new[] { 1, 2, 3 })));
        Assert.That(queue, Is.EqualTo(new[] { 1, 2, 3 }));
        NexusFormatterRegistry.Register(new StackFormatter<int>());
        var stack = new Stack<int>(new[] { 1, 2, 3 });
        Assert.That(TestHelpers.Deserialize<Stack<int>>(TestHelpers.Serialize(stack)), Is.EqualTo(stack));
        NexusFormatterRegistry.Register(new KeyValuePairFormatter<string, int>());
        RoundTrip(new KeyValuePair<string, int>("k", 3));
        NexusFormatterRegistry.Register(new ValueTupleFormatter<int, string, double>());
        RoundTrip((1, "two", 3.0));
    }

    [Test]
    public void DeserializedDictionaryUsesRandomizedComparerWhenUntrusted()
    {
        var bytes = TestHelpers.Serialize(new Dictionary<int, string> { [1] = "a" });
        var untrusted = TestHelpers.Deserialize<Dictionary<int, string>>(bytes, NexusSerializerOptions.Untrusted)!;
        var trusted = TestHelpers.Deserialize<Dictionary<int, string>>(bytes, NexusSerializerOptions.Trusted)!;
        Assert.That(untrusted.Comparer, Is.Not.SameAs(EqualityComparer<int>.Default));
        Assert.That(trusted.Comparer, Is.SameAs(EqualityComparer<int>.Default));
        Assert.That(untrusted[1], Is.EqualTo("a"));
    }

    [Test]
    public void EnumsUseUnderlyingInteger()
    {
        var formatter = EnumFormatter<DayOfWeek>.Instance;
        var bytes = Write((ref MsgPackWriter w) => formatter.Serialize(ref w, DayOfWeek.Friday));
        Assert.That(Hex(bytes), Is.EqualTo("05"));
        var reader = new MsgPackReader(bytes);
        DayOfWeek day = default;
        formatter.Deserialize(ref reader, ref day);
        Assert.That(day, Is.EqualTo(DayOfWeek.Friday));
    }

    [Test]
    public void MissingFormatterThrowsDescriptiveException()
    {
        var ex = Assert.Throws<NexusSerializationException>(() => NexusFormatterRegistry.Get<FormatterTests>());
        Assert.That(ex!.Message, Does.Contain("NexusObject"));
    }

    // ------------------------------------------------------------------ Cross-checks with MessagePack-CSharp

    [Test]
    public void ReferenceReadsNexNetOutput()
    {
        Assert.That(MessagePackSerializer.Deserialize<string[]>(TestHelpers.Serialize(new[] { "a", "bc" })), Is.EqualTo(new[] { "a", "bc" }));
        Assert.That(MessagePackSerializer.Deserialize<Dictionary<string, int>>(TestHelpers.Serialize(new Dictionary<string, int> { ["x"] = 300 })),
            Is.EqualTo(new Dictionary<string, int> { ["x"] = 300 }));
        Assert.That(MessagePackSerializer.Deserialize<long>(TestHelpers.Serialize(long.MinValue)), Is.EqualTo(long.MinValue));
        Assert.That(MessagePackSerializer.Deserialize<byte[]>(TestHelpers.Serialize(new byte[] { 9, 8 })), Is.EqualTo(new byte[] { 9, 8 }));
        Assert.That(MessagePackSerializer.Deserialize<TimeSpan>(TestHelpers.Serialize(TimeSpan.FromTicks(12345))), Is.EqualTo(TimeSpan.FromTicks(12345)));
    }

    [Test]
    public void NexNetReadsReferenceOutput()
    {
        Assert.That(TestHelpers.Deserialize<string[]>(MessagePackSerializer.Serialize(new[] { "a", null, "c" })), Is.EqualTo(new[] { "a", null, "c" }));
        Assert.That(TestHelpers.Deserialize<Dictionary<int, string>>(MessagePackSerializer.Serialize(new Dictionary<int, string> { [7] = "seven" })),
            Is.EqualTo(new Dictionary<int, string> { [7] = "seven" }));
        Assert.That(TestHelpers.Deserialize<ulong>(MessagePackSerializer.Serialize(ulong.MaxValue)), Is.EqualTo(ulong.MaxValue));
        Assert.That(TestHelpers.Deserialize<bool[]>(MessagePackSerializer.Serialize(new[] { true, false })), Is.EqualTo(new[] { true, false }));
    }

    [Test]
    public void PrimitiveArrayExtIsReadableByReferenceAsExt()
    {
        var bytes = TestHelpers.Serialize(new[] { 1, 2, -3 });
        var reader = new MessagePackReader(bytes);
        var ext = reader.ReadExtensionFormat();
        Assert.That(ext.Header.TypeCode, Is.EqualTo(NexusExtType.PrimitiveArray));
        var data = ext.Data.ToArray();
        Assert.That(data[0], Is.EqualTo((byte)PrimitiveKind.Int32));
        Assert.That(Hex(data[1..]), Is.EqualTo("01000000" + "02000000" + "fdffffff"));
    }
}
