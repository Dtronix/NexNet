using MessagePack;
using NexNet.Serialization;
using static NexNet.Serialization.Tests.TestHelpers;

[assembly: NexusSerializable<NexNet.Serialization.Tests.Envelope<int>>]
[assembly: NexusSerializable<NexNet.Serialization.Tests.Envelope<NexNet.Serialization.Tests.GenPerson>>]
[assembly: NexusFormatter<NexNet.Serialization.Tests.ThirdPartyFormatter, NexNet.Serialization.Tests.ThirdParty>]

namespace NexNet.Serialization.Tests;

[NexusObject]
public class GenPerson
{
    [NexusKey(0)] public int Id { get; set; }
    [NexusKey(1)] public string? Name { get; set; }
    [NexusKey(3)] public List<string>? Tags { get; set; }
    [NexusKey(4)] public double[]? Scores { get; set; }
    [NexusIgnore] public object? Cache { get; set; }
}

[NexusObject]
public struct GenPoint
{
    [NexusKey(0)] public int X { get; set; }
    [NexusKey(1)] public int Y;
}

[NexusObject]
public record GenPair([property: NexusKey(0)] int A, [property: NexusKey(1)] string B);

[NexusObject]
public class GenSecret
{
    [NexusKey(0)] private int _hidden;
    [NexusKey(1)] public string? Name { get; private set; }
    [NexusKey(2)] public int ReadOnlyAuto { get; }

    public GenSecret() { }

    public GenSecret(int hidden, string name, int readOnlyAuto)
    {
        _hidden = hidden;
        Name = name;
        ReadOnlyAuto = readOnlyAuto;
    }

    public int Hidden => _hidden;
}

[NexusObject]
public class GenInit
{
    [NexusKey(0)] public required int Id { get; init; }
    [NexusKey(1)] public string? Label { get; init; }
}

[NexusObject]
[NexusUnion<GenCircle>(0)]
[NexusUnion<GenSquare>(1)]
public interface IGenShape { }

[NexusObject]
public class GenCircle : IGenShape { [NexusKey(0)] public double Radius { get; set; } }

[NexusObject]
public class GenSquare : IGenShape { [NexusKey(0)] public double Side { get; set; } }

[NexusObject]
public class Envelope<T> { [NexusKey(0)] public T? Value { get; set; } }

public class ThirdParty { public int Value; }

public sealed class ThirdPartyFormatter : NexusFormatter<ThirdParty>
{
    public override void Serialize(ref MsgPackWriter writer, ThirdParty? value) => writer.Write(value?.Value ?? -1);
    public override void Deserialize(ref MsgPackReader reader, ref ThirdParty? value) => value = new ThirdParty { Value = reader.ReadInt32() };
}

/// <summary>
/// Reference mirror used to check that MessagePack-CSharp reads NexNet object output.
/// </summary>
[MessagePackObject]
public class GenPersonMirror
{
    [Key(0)] public int Id { get; set; }
    [Key(1)] public string? Name { get; set; }
    [Key(3)] public List<string>? Tags { get; set; }
}

public class GeneratedFormatterTests
{
    [Test]
    public async Task ObjectRoundTripsWithKeyGap()
    {
        var person = new GenPerson { Id = 7, Name = "seven", Tags = ["a", "b"], Scores = [1.5, 2.5], Cache = new object() };
        var bytes = TestHelpers.Serialize(person);

        // array(5): 07, "seven", nil (key 2 unused), ["a","b"], ext78
        await Assert.That(Hex(bytes).StartsWith("9507a5736576656ec092a161a162")).IsTrue().Because(Hex(bytes));

        var result = TestHelpers.Deserialize<GenPerson>(bytes)!;
        await Assert.That(result.Id).IsEqualTo(7);
        await Assert.That(result.Name).IsEqualTo("seven");
        await Assert.That(result.Tags).IsEquivalentTo(new[] { "a", "b" }, CollectionOrdering.Matching);
        await Assert.That(result.Scores).IsEquivalentTo(new[] { 1.5, 2.5 }, CollectionOrdering.Matching);
        await Assert.That(result.Cache).IsNull();
    }

    [Test]
    public async Task ReferenceReadsGeneratedObject()
    {
        var person = new GenPerson { Id = 3, Name = "x", Tags = ["t"] };
        var bytes = TestHelpers.Serialize(person);

        // The reference reader skips the unknown ext 78 at key 4 via the mirror's missing key.
        var mirror = MessagePackSerializer.Deserialize<GenPersonMirror>(bytes);
        await Assert.That(mirror.Id).IsEqualTo(3);
        await Assert.That(mirror.Name).IsEqualTo("x");
        await Assert.That(mirror.Tags).IsEquivalentTo(new[] { "t" }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task GeneratedObjectReadsReferenceOutput()
    {
        var bytes = MessagePackSerializer.Serialize(new GenPersonMirror { Id = 11, Name = "ref", Tags = ["q"] });
        var person = TestHelpers.Deserialize<GenPerson>(bytes)!;
        await Assert.That(person.Id).IsEqualTo(11);
        await Assert.That(person.Name).IsEqualTo("ref");
        await Assert.That(person.Tags).IsEquivalentTo(new[] { "q" }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task PopulateResetsMembersMissingFromPayload()
    {
        var formatter = NexusFormatterRegistry.Get<GenPerson>();
        var existing = new GenPerson { Id = 1, Name = "old", Tags = ["stale"] };

        // Payload only carries key 0.
        var bytes = Write((ref MsgPackWriter w) =>
        {
            w.WriteArrayHeader(1);
            w.Write(42);
        });

        var reader = new MsgPackReader(bytes);
        GenPerson? value = existing;
        formatter.Deserialize(ref reader, ref value);
        await Assert.That(value).IsSameReferenceAs(existing).Because("instance is reused");
        await Assert.That(value!.Id).IsEqualTo(42);
        await Assert.That(value.Name).IsNull();
        await Assert.That(value.Tags).IsNull();
    }

    [Test]
    public async Task ExtraMembersFromNewerPeerAreSkipped()
    {
        // array(7): keys 0-4 as this version knows them, plus two unknown trailing members.
        var bytes = Write((ref MsgPackWriter w) =>
        {
            w.WriteArrayHeader(7);
            w.Write(9);
            w.Write("newer");
            w.WriteNil();
            w.WriteArrayHeader(1);
            w.Write("t");
            w.WriteNil();
            w.WriteMapHeader(1);
            w.Write("k");
            w.WriteArrayHeader(2);
            w.Write(1);
            w.Write(2);
            w.Write("extra");
        });

        var reader = new MsgPackReader(bytes);
        GenPerson? value = null;
        NexusFormatterRegistry.Get<GenPerson>().Deserialize(ref reader, ref value);
        await Assert.That(reader.End).IsTrue();
        await Assert.That(value!.Id).IsEqualTo(9);
        await Assert.That(value.Name).IsEqualTo("newer");
        await Assert.That(value.Tags).IsEquivalentTo(new[] { "t" }, CollectionOrdering.Matching);
        await Assert.That(value.Scores).IsNull();
    }

    [Test]
    public async Task StructRoundTrips()
    {
        var point = new GenPoint { X = -3, Y = 9 };
        var result = TestHelpers.Deserialize<GenPoint>(TestHelpers.Serialize(point));
        await Assert.That(result.X).IsEqualTo(-3);
        await Assert.That(result.Y).IsEqualTo(9);
    }

    [Test]
    public async Task RecordWithConstructorRoundTrips()
    {
        var pair = new GenPair(5, "five");
        await Assert.That(TestHelpers.Deserialize<GenPair>(TestHelpers.Serialize(pair))).IsEqualTo(pair);
    }

    [Test]
    public async Task PrivateMembersRoundTrip()
    {
        var secret = new GenSecret(13, "n", 99);
        var result = TestHelpers.Deserialize<GenSecret>(TestHelpers.Serialize(secret))!;
        await Assert.That(result.Hidden).IsEqualTo(13);
        await Assert.That(result.Name).IsEqualTo("n");
        await Assert.That(result.ReadOnlyAuto).IsEqualTo(99);
    }

    [Test]
    public async Task InitAndRequiredMembersRoundTrip()
    {
        var init = new GenInit { Id = 4, Label = "four" };
        var result = TestHelpers.Deserialize<GenInit>(TestHelpers.Serialize(init))!;
        await Assert.That(result.Id).IsEqualTo(4);
        await Assert.That(result.Label).IsEqualTo("four");
    }

    [Test]
    public async Task UnionRoundTrips()
    {
        IGenShape circle = new GenCircle { Radius = 2 };
        IGenShape square = new GenSquare { Side = 3 };

        var circleBytes = TestHelpers.Serialize(circle);
        await Assert.That(Hex(circleBytes).StartsWith("920091cb")).IsTrue().Because(Hex(circleBytes));

        await Assert.That(((GenCircle)TestHelpers.Deserialize<IGenShape>(circleBytes)!).Radius).IsEqualTo(2);
        await Assert.That(((GenSquare)TestHelpers.Deserialize<IGenShape>(TestHelpers.Serialize(square))!).Side).IsEqualTo(3);
        await Assert.That(TestHelpers.Deserialize<IGenShape>(TestHelpers.Serialize<IGenShape?>(null))).IsNull();
    }

    [Test]
    public async Task UnknownUnionTagThrows()
    {
        var bytes = new byte[] { 0x92, 0x05, 0x90 };
        await Assert.That(() => TestHelpers.Deserialize<IGenShape>(bytes)).ThrowsExactly<NexusSerializationException>();
    }

    [Test]
    public async Task GenericTypesFromAssemblyDeclarationsRoundTrip()
    {
        var envelope = new Envelope<int> { Value = 12 };
        await Assert.That(TestHelpers.Deserialize<Envelope<int>>(TestHelpers.Serialize(envelope))!.Value).IsEqualTo(12);

        var personEnvelope = new Envelope<GenPerson> { Value = new GenPerson { Id = 2 } };
        await Assert.That(TestHelpers.Deserialize<Envelope<GenPerson>>(TestHelpers.Serialize(personEnvelope))!.Value!.Id).IsEqualTo(2);
    }

    [Test]
    public async Task UserFormatterIsRegistered()
    {
        var bytes = TestHelpers.Serialize(new ThirdParty { Value = 77 });
        await Assert.That(Hex(bytes)).IsEqualTo("4d");
        await Assert.That(TestHelpers.Deserialize<ThirdParty>(bytes)!.Value).IsEqualTo(77);
    }
}
