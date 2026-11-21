using System.Numerics;
using System.Text.Json.Nodes;
using NexNet.Interop;
using NexNet.Serialization;

namespace NexNet.Generator.Tests.Targets.CSharpTest;

/// <summary>The typed-JSON value model of the scaffolding codecs (NexNet.Interop.Abstractions), over built-in formatters.</summary>
public class TypedJsonTests
{
    private static TypeSchema P(SchemaPrim prim) => TypeSchema.Primitive(prim);

    private static async Task RoundTrip<T>(T value, TypeSchema schema, string json)
    {
        var codec = new TypedCodec<T>(schema, SchemaSet.Empty);
        var encoded = codec.Encode(value);
        await Assert.That(encoded?.ToJsonString() ?? "null").IsEqualTo(json);
        var decoded = codec.Decode(JsonNode.Parse(json));
        await Assert.That(NexusSerializer.Serialize(decoded)).IsEquivalentTo(NexusSerializer.Serialize(value));
    }

    [Test]
    public async Task Integers()
    {
        await RoundTrip(-5, P(SchemaPrim.Int32), "-5");
        await RoundTrip((byte)255, P(SchemaPrim.Byte), "255");
        await RoundTrip(9007199254740991L, P(SchemaPrim.Int64), "9007199254740991");
        await RoundTrip(9007199254740992L, P(SchemaPrim.Int64), "\"9007199254740992\"");
        await RoundTrip(long.MinValue, P(SchemaPrim.Int64), "\"-9223372036854775808\"");
        await RoundTrip(ulong.MaxValue, P(SchemaPrim.UInt64), "\"18446744073709551615\"");
        await RoundTrip('x', P(SchemaPrim.Char), "120");
        await RoundTrip(true, P(SchemaPrim.Bool), "true");
    }

    [Test]
    public async Task FloatsAreBitPatterns()
    {
        await RoundTrip(1.0f, P(SchemaPrim.Single), "\"f32:0x3F800000\"");
        await RoundTrip(-0.0, P(SchemaPrim.Double), "\"f64:0x8000000000000000\"");
        await RoundTrip(double.NaN, P(SchemaPrim.Double), "\"f64:0xFFF8000000000000\"");
        await RoundTrip((Half)1.5, P(SchemaPrim.Half), "\"f16:0x3E00\"");

        // Numbers are accepted on input.
        var codec = new TypedCodec<double>(P(SchemaPrim.Double), SchemaSet.Empty);
        await Assert.That(codec.Decode(JsonNode.Parse("2.5"))).IsEqualTo(2.5);
    }

    [Test]
    public async Task ValueTypesAreRawFields()
    {
        await RoundTrip(-1.250m, P(SchemaPrim.Decimal), "[1250,0,0,-2147287040]");
        await RoundTrip(new Guid("00112233-4455-6677-8899-aabbccddeeff"), P(SchemaPrim.Guid), "\"00112233-4455-6677-8899-aabbccddeeff\"");
        var utc = new DateTime(2020, 1, 2, 0, 0, 0, DateTimeKind.Utc);
        await RoundTrip(utc, P(SchemaPrim.DateTime), "\"" + utc.ToBinary() + "\"");
        await RoundTrip(new DateTimeOffset(2020, 1, 2, 0, 0, 0, TimeSpan.FromMinutes(-90)), P(SchemaPrim.DateTimeOffset), "[\"637135254000000000\",-90]");
        await RoundTrip(TimeSpan.FromSeconds(1), P(SchemaPrim.TimeSpan), "10000000");
        await RoundTrip(new DateOnly(2020, 1, 2), P(SchemaPrim.DateOnly), "737425");
        await RoundTrip(BigInteger.Parse("-123456789012345678901234567890"), P(SchemaPrim.BigInteger), "\"-123456789012345678901234567890\"");
        await RoundTrip(new Version(1, 2, 3), P(SchemaPrim.Version), "\"1.2.3\"");
    }

    [Test]
    public async Task ContainersAndNulls()
    {
        await RoundTrip<string?>(null, P(SchemaPrim.String), "null");
        await RoundTrip<int?>(null, TypeSchema.Nullable(P(SchemaPrim.Int32)), "null");
        await RoundTrip<int?>(4, TypeSchema.Nullable(P(SchemaPrim.Int32)), "4");
        await RoundTrip(new byte[] { 1, 2, 3 }, TypeSchema.Bin(), "\"AQID\"");
        await RoundTrip(new[] { 1, -1 }, TypeSchema.PrimArray(SchemaPrim.Int32), "[1,-1]");
        await RoundTrip(new[] { 1.0f }, TypeSchema.PrimArray(SchemaPrim.Single), "[\"f32:0x3F800000\"]");
        await RoundTrip(new List<string> { "a", "b" }, TypeSchema.Seq(P(SchemaPrim.String), "List"), "[\"a\",\"b\"]");
        await RoundTrip(new Dictionary<string, int> { ["a"] = 1 }, TypeSchema.Map(P(SchemaPrim.String), P(SchemaPrim.Int32)), "[[\"a\",1]]");

        // A sequence whose bound element is an ext 78 primitive uses the primitive array form.
        await RoundTrip(new List<int> { 7 }, TypeSchema.Seq(P(SchemaPrim.Int32), "List"), "[7]");
    }

    [Test]
    public async Task MismatchesAreFormatErrors()
    {
        var codec = new TypedCodec<int>(P(SchemaPrim.Int32), SchemaSet.Empty);
        await Assert.That(() => codec.Decode(JsonNode.Parse("\"x\""))).Throws<FormatException>();
        await Assert.That(() => TypedJson.FromMsgPack(NexusSerializer.Serialize("text"), P(SchemaPrim.Int32), SchemaSet.Empty)).Throws<Exception>();
        await Assert.That(() => TypedJson.ToMsgPack(JsonNode.Parse("[1]"), TypeSchema.Decl("Missing"), SchemaSet.Empty)).Throws<KeyNotFoundException>();
    }
}
