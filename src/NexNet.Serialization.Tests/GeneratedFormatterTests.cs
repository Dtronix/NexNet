#if !NEXNET_MEMORYPACK
// Generated formatters exist only with the MessagePack payload backend.
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

[TestFixture]
public class GeneratedFormatterTests
{
    [Test]
    public void ObjectRoundTripsWithKeyGap()
    {
        var person = new GenPerson { Id = 7, Name = "seven", Tags = ["a", "b"], Scores = [1.5, 2.5], Cache = new object() };
        var bytes = TestHelpers.Serialize(person);

        // array(5): 07, "seven", nil (key 2 unused), ["a","b"], ext78
        Assert.That(Hex(bytes).StartsWith("9507a5736576656ec092a161a162"), Is.True, Hex(bytes));

        var result = TestHelpers.Deserialize<GenPerson>(bytes)!;
        Assert.That(result.Id, Is.EqualTo(7));
        Assert.That(result.Name, Is.EqualTo("seven"));
        Assert.That(result.Tags, Is.EqualTo(new[] { "a", "b" }));
        Assert.That(result.Scores, Is.EqualTo(new[] { 1.5, 2.5 }));
        Assert.That(result.Cache, Is.Null);
    }

    [Test]
    public void ReferenceReadsGeneratedObject()
    {
        var person = new GenPerson { Id = 3, Name = "x", Tags = ["t"] };
        var bytes = TestHelpers.Serialize(person);

        // The reference reader skips the unknown ext 78 at key 4 via the mirror's missing key.
        var mirror = MessagePackSerializer.Deserialize<GenPersonMirror>(bytes);
        Assert.That(mirror.Id, Is.EqualTo(3));
        Assert.That(mirror.Name, Is.EqualTo("x"));
        Assert.That(mirror.Tags, Is.EqualTo(new[] { "t" }));
    }

    [Test]
    public void GeneratedObjectReadsReferenceOutput()
    {
        var bytes = MessagePackSerializer.Serialize(new GenPersonMirror { Id = 11, Name = "ref", Tags = ["q"] });
        var person = TestHelpers.Deserialize<GenPerson>(bytes)!;
        Assert.That(person.Id, Is.EqualTo(11));
        Assert.That(person.Name, Is.EqualTo("ref"));
        Assert.That(person.Tags, Is.EqualTo(new[] { "q" }));
    }

    [Test]
    public void PopulateResetsMembersMissingFromPayload()
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
        Assert.That(value, Is.SameAs(existing), "instance is reused");
        Assert.That(value!.Id, Is.EqualTo(42));
        Assert.That(value.Name, Is.Null);
        Assert.That(value.Tags, Is.Null);
    }

    [Test]
    public void ExtraMembersFromNewerPeerAreSkipped()
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
        Assert.That(reader.End, Is.True);
        Assert.That(value!.Id, Is.EqualTo(9));
        Assert.That(value.Name, Is.EqualTo("newer"));
        Assert.That(value.Tags, Is.EqualTo(new[] { "t" }));
        Assert.That(value.Scores, Is.Null);
    }

    [Test]
    public void StructRoundTrips()
    {
        var point = new GenPoint { X = -3, Y = 9 };
        var result = TestHelpers.Deserialize<GenPoint>(TestHelpers.Serialize(point));
        Assert.That(result.X, Is.EqualTo(-3));
        Assert.That(result.Y, Is.EqualTo(9));
    }

    [Test]
    public void RecordWithConstructorRoundTrips()
    {
        var pair = new GenPair(5, "five");
        Assert.That(TestHelpers.Deserialize<GenPair>(TestHelpers.Serialize(pair)), Is.EqualTo(pair));
    }

    [Test]
    public void PrivateMembersRoundTrip()
    {
        var secret = new GenSecret(13, "n", 99);
        var result = TestHelpers.Deserialize<GenSecret>(TestHelpers.Serialize(secret))!;
        Assert.That(result.Hidden, Is.EqualTo(13));
        Assert.That(result.Name, Is.EqualTo("n"));
        Assert.That(result.ReadOnlyAuto, Is.EqualTo(99));
    }

    [Test]
    public void InitAndRequiredMembersRoundTrip()
    {
        var init = new GenInit { Id = 4, Label = "four" };
        var result = TestHelpers.Deserialize<GenInit>(TestHelpers.Serialize(init))!;
        Assert.That(result.Id, Is.EqualTo(4));
        Assert.That(result.Label, Is.EqualTo("four"));
    }

    [Test]
    public void UnionRoundTrips()
    {
        IGenShape circle = new GenCircle { Radius = 2 };
        IGenShape square = new GenSquare { Side = 3 };

        var circleBytes = TestHelpers.Serialize(circle);
        Assert.That(Hex(circleBytes).StartsWith("920091cb"), Is.True, Hex(circleBytes));

        Assert.That(((GenCircle)TestHelpers.Deserialize<IGenShape>(circleBytes)!).Radius, Is.EqualTo(2));
        Assert.That(((GenSquare)TestHelpers.Deserialize<IGenShape>(TestHelpers.Serialize(square))!).Side, Is.EqualTo(3));
        Assert.That(TestHelpers.Deserialize<IGenShape>(TestHelpers.Serialize<IGenShape?>(null)), Is.Null);
    }

    [Test]
    public void UnknownUnionTagThrows()
    {
        var bytes = new byte[] { 0x92, 0x05, 0x90 };
        Assert.Throws<NexusSerializationException>(() => TestHelpers.Deserialize<IGenShape>(bytes));
    }

    [Test]
    public void GenericTypesFromAssemblyDeclarationsRoundTrip()
    {
        var envelope = new Envelope<int> { Value = 12 };
        Assert.That(TestHelpers.Deserialize<Envelope<int>>(TestHelpers.Serialize(envelope))!.Value, Is.EqualTo(12));

        var personEnvelope = new Envelope<GenPerson> { Value = new GenPerson { Id = 2 } };
        Assert.That(TestHelpers.Deserialize<Envelope<GenPerson>>(TestHelpers.Serialize(personEnvelope))!.Value!.Id, Is.EqualTo(2));
    }

    [Test]
    public void UserFormatterIsRegistered()
    {
        var bytes = TestHelpers.Serialize(new ThirdParty { Value = 77 });
        Assert.That(Hex(bytes), Is.EqualTo("4d"));
        Assert.That(TestHelpers.Deserialize<ThirdParty>(bytes)!.Value, Is.EqualTo(77));
    }
}
#endif
