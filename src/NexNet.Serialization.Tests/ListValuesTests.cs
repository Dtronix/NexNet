using NexNet.Internals;
using static NexNet.Serialization.Tests.TestHelpers;

namespace NexNet.Serialization.Tests;

/// <summary>
/// The embedded values of a collection ResetValues item (protocol specification §4.11): a chunk of list items written
/// as the item type's `T[]` form, for item types with and without a registered `Memory&lt;T&gt;` formatter (I35).
/// </summary>
public class ListValuesTests
{
    [Test]
    public async Task PrimitiveValues_MatchTheGoldenExt78()
    {
        // tests/golden/collections.jsonl collection.reset-values.int and .int.empty.
        await Assert.That(Hex(PayloadSerializer.SerializeValues(new Memory<int>([1, 2, 3])))).IsEqualTo("c70d4e04010000000200000003000000");
        await Assert.That(Hex(PayloadSerializer.SerializeValues(Memory<int>.Empty))).IsEqualTo("d44e04");
    }

    [Test]
    public async Task PrimitiveValues_AreTheMemoryFormatterBytes()
    {
        long[] longs = [1, -2, long.MaxValue];
        await Assert.That(PayloadSerializer.SerializeValues(longs.AsMemory())).IsEquivalentTo(PayloadSerializer.Serialize(longs.AsMemory()), CollectionOrdering.Matching);
        byte[] bytes = [1, 2, 255];
        await Assert.That(PayloadSerializer.SerializeValues(bytes.AsMemory())).IsEquivalentTo(PayloadSerializer.Serialize(bytes), CollectionOrdering.Matching);
    }

    [Test]
    public async Task StringValues_MatchTheGoldenArray()
    {
        // tests/golden/collections.jsonl collection.reset-values.string.
        string[] strings = ["a", "b"];
        await Assert.That(Hex(PayloadSerializer.SerializeValues(strings.AsMemory()))).IsEqualTo("92a161a162");
    }

    [Test]
    public async Task StringValues_RoundTrip()
    {
        string?[] strings = ["a", null, "été"];
        var bytes = PayloadSerializer.SerializeValues(strings.AsMemory());
        await Assert.That(bytes).IsEquivalentTo(PayloadSerializer.Serialize(strings), CollectionOrdering.Matching);
        await Assert.That(PayloadSerializer.DeserializeValues<string?>(bytes, NexusSerializerOptions.Untrusted)).IsEquivalentTo(strings, CollectionOrdering.Matching);
    }

    [Test]
    public async Task NexusObjectValues_AreAnArrayOfItems()
    {
        // No Memory<T> or T[] formatter is registered for these item types; the values are the T[] array form.
        await Assert.That(NexusFormatterRegistry.TryGet<Memory<GenPoint>>(out _)).IsFalse();
        await Assert.That(NexusFormatterRegistry.TryGet<GenPoint[]>(out _)).IsFalse();

        GenPoint[] points = [new() { X = 1, Y = 2 }, new() { X = -3, Y = 4 }];
        var bytes = PayloadSerializer.SerializeValues(points.AsMemory());
        var expected = new List<byte> { 0x92 };
        foreach (var point in points)
            expected.AddRange(PayloadSerializer.Serialize(point));
        await Assert.That(bytes).IsEquivalentTo(expected, CollectionOrdering.Matching);
        await Assert.That(PayloadSerializer.DeserializeValues<GenPoint>(bytes, NexusSerializerOptions.Untrusted)).IsEquivalentTo(points, CollectionOrdering.Matching);

        GenPerson[] people = [new() { Id = 1, Name = "a" }, new() { Id = 2 }];
        var read = PayloadSerializer.DeserializeValues<GenPerson>(PayloadSerializer.SerializeValues(people.AsMemory()), NexusSerializerOptions.Untrusted)!;
        await Assert.That(read.Select(p => (p.Id, p.Name))).IsEquivalentTo(people.Select(p => (p.Id, p.Name)), CollectionOrdering.Matching);
    }

    [Test]
    public async Task EmptyNexusObjectValues_AreAnEmptyArray()
    {
        await Assert.That(Hex(PayloadSerializer.SerializeValues(Memory<GenPoint>.Empty))).IsEqualTo("90");
        await Assert.That(PayloadSerializer.DeserializeValues<GenPoint>(new byte[] { 0x90 }, NexusSerializerOptions.Untrusted)).IsEmpty();
    }
}
