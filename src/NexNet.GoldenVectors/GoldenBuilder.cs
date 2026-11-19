using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using NexNet.Interop;
using NexNet.Serialization;

namespace NexNet.GoldenVectors;

/// <summary>
/// Collects golden vectors and checks each one against the C# implementation as it is added: a vector the C# code does
/// not produce, accept or reject as stated is a generation error.
/// </summary>
public sealed partial class GoldenBuilder
{
    private readonly List<GoldenVector> _vectors = new();
    private readonly Dictionary<string, GoldenNative> _natives = new(StringComparer.Ordinal);
    private readonly HashSet<string> _ids = new(StringComparer.Ordinal);
    private string _file = "";

    /// <summary>The file every reject vector goes to.</summary>
    public const string RejectFile = "reject";

    /// <summary>Creates a builder over <paramref name="schemas"/>.</summary>
    public GoldenBuilder(GoldenSchemas schemas) => Schemas = schemas;

    /// <summary>The declared types the vectors use.</summary>
    public GoldenSchemas Schemas { get; }

    /// <summary>The vectors, in file and insertion order.</summary>
    public IReadOnlyList<GoldenVector> Vectors => _vectors;

    /// <summary>The C# implementation of every type expression with a NexNet formatter.</summary>
    public IReadOnlyDictionary<string, GoldenNative> Natives => _natives;

    /// <summary>Starts the vectors of <paramref name="name"/>.jsonl.</summary>
    /// <remarks>Reject vectors always go to <see cref="RejectFile"/>, whatever file is current.</remarks>
    public void File(string name) => _file = name != RejectFile ? name : throw new ArgumentException("Reject vectors go to reject.jsonl on their own.", nameof(name));

    /// <summary>A canonical vector of <paramref name="value"/>, encoded by its NexNet formatter.</summary>
    public void Both<T>(string type, string id, T value, string? note = null, GoldenOptions? options = null)
    {
        var native = Native<T>(type);
        var bytes = NexusSerializer.Serialize(value);
        var json = ToJson(id, type, bytes);
        Require(id, native.Reencode(bytes, options?.ToSerializerOptions() ?? NexusSerializerOptions.Untrusted).AsSpan().SequenceEqual(bytes),
            "the C# formatter does not read back its own encoding");
        Add(new GoldenVector(_file, id, type, GoldenMode.Both, json, bytes, options, null, note));
    }

    /// <summary>Accepted, non-canonical input: the value is what the C# formatter decodes from <paramref name="hex"/>.</summary>
    public void Decode<T>(string type, string id, string hex, string? note = null, GoldenOptions? options = null)
    {
        var native = Native<T>(type);
        var bytes = Hex(hex);
        byte[] canonical;
        try
        {
            canonical = native.Reencode(bytes, options?.ToSerializerOptions() ?? NexusSerializerOptions.Untrusted);
        }
        catch (NexusSerializationException e)
        {
            throw Fail(id, "the C# formatter rejects it: " + e.Message);
        }

        Require(id, !canonical.AsSpan().SequenceEqual(bytes), "the input is canonical; make it a both vector");
        Add(new GoldenVector(_file, id, type, GoldenMode.Decode, ToJson(id, type, canonical), bytes, options, null, note));
    }

    /// <summary>Input the C# formatter rejects.</summary>
    public void Reject<T>(string type, string id, string hex, string error, string? note = null, GoldenOptions? options = null)
    {
        var native = Native<T>(type);
        var bytes = Hex(hex);
        try
        {
            native.Reencode(bytes, options?.ToSerializerOptions() ?? NexusSerializerOptions.Untrusted);
        }
        catch (NexusSerializationException)
        {
            AddReject(type, id, bytes, error, note, options);
            return;
        }
        catch (Exception e)
        {
            throw Fail(id, "the C# formatter throws " + e.GetType().Name + " instead of NexusSerializationException: " + e.Message);
        }

        throw Fail(id, "the C# formatter accepts it");
    }

    /// <summary>A canonical vector of a type without a NexNet formatter here (message bodies), from its typed JSON.</summary>
    public void BothJson(string type, string id, string json, string? note = null)
    {
        var value = JsonNode.Parse(json);
        var bytes = TypedJson.ToMsgPack(value, Schemas.Parse(type), Schemas.Set);
        var back = ToJson(id, type, bytes);
        Require(id, GoldenFormat.Canonical(back) == GoldenFormat.Canonical(value), "the value is not in canonical typed JSON: " + GoldenFormat.Canonical(back));
        Add(new GoldenVector(_file, id, type, GoldenMode.Both, back, bytes, null, null, note));
    }

    /// <summary>Accepted, non-canonical input of a type without a NexNet formatter here; checked by the C# replay.</summary>
    public void DecodeJson(string type, string id, string hex, string json, string? note = null)
    {
        var value = JsonNode.Parse(json);
        var bytes = Hex(hex);
        var canonical = TypedJson.ToMsgPack(value, Schemas.Parse(type), Schemas.Set);
        Require(id, !canonical.AsSpan().SequenceEqual(bytes), "the input is canonical; make it a both vector");
        Add(new GoldenVector(_file, id, type, GoldenMode.Decode, ToJson(id, type, canonical), bytes, null, null, note));
    }

    /// <summary>Input of a type without a NexNet formatter here that must be rejected; checked by the C# replay.</summary>
    public void RejectJson(string type, string id, string hex, string error, string? note = null)
    {
        Schemas.Parse(type);
        AddReject(type, id, Hex(hex), error, note, null);
    }

    /// <summary>A frame (protocol specification §2), from its typed JSON (<see cref="FrameCodec"/>).</summary>
    public void Frame(string id, string json, string? note = null)
    {
        var value = JsonNode.Parse(json);
        var bytes = FrameCodec.Encode(value, Schemas);
        var back = FrameCodec.Decode(bytes, Schemas);
        Require(id, GoldenFormat.Canonical(back) == GoldenFormat.Canonical(value), "the value is not canonical: " + GoldenFormat.Canonical(back));
        Add(new GoldenVector(_file, id, FrameCodec.FrameType, GoldenMode.Both, back, bytes, null, null, note));
    }

    /// <summary>The connection preamble (protocol specification §1).</summary>
    public void Preamble(string id, string json, string? note = null)
    {
        var value = JsonNode.Parse(json);
        var bytes = FrameCodec.EncodePreamble(value);
        Add(new GoldenVector(_file, id, FrameCodec.PreambleType, GoldenMode.Both, FrameCodec.DecodePreamble(bytes), bytes, null, null, note));
    }

    /// <summary>Bytes from hex; spaces are ignored.</summary>
    public static byte[] Hex(string hex) => Convert.FromHexString(hex.Replace(" ", "", StringComparison.Ordinal));

    private void AddReject(string type, string id, byte[] bytes, string error, string? note, GoldenOptions? options)
    {
        Require(id, GoldenFormat.ErrorKinds.Contains(error), "unknown error kind '" + error + "'");
        Add(new GoldenVector(RejectFile, id, type, GoldenMode.Reject, null, bytes, options, error, note));
    }

    private JsonNode? ToJson(string id, string type, byte[] canonical)
    {
        var schema = Schemas.Parse(type);
        JsonNode? json;
        try
        {
            json = TypedJson.FromMsgPack(canonical, schema, Schemas.Set);
        }
        catch (Exception e) when (e is FormatException or NexusSerializationException)
        {
            throw Fail(id, "the typed-JSON model cannot read " + Convert.ToHexStringLower(canonical) + ": " + e.Message);
        }

        Require(id, TypedJson.ToMsgPack(json, schema, Schemas.Set).AsSpan().SequenceEqual(canonical),
            "the typed-JSON model does not write the same bytes back");
        return json;
    }

    private GoldenNative Native<T>(string type)
    {
        if (_natives.TryGetValue(type, out var existing))
        {
            if (existing.ClrType != typeof(T))
                throw new InvalidOperationException("Type '" + type + "' is used for both " + existing.ClrType + " and " + typeof(T) + ".");
            return existing;
        }

        // The schema of the expression must be the one the generator derives for the C# type.
        var schema = Schemas.Parse(type);
        if (TypedCodecs.TryGet<T>(out var codec))
        {
            var generated = TypeExpr.Print(GoldenSchemas.Rename(codec!.Schema));
            if (generated != TypeExpr.Print(schema))
                throw new InvalidOperationException("Type '" + type + "' is " + TypeExpr.Print(schema) + ", but the generator gives " + typeof(T) + " the schema " + generated + ".");
        }

        var native = new FormatterNative<T>();
        _natives.Add(type, native);
        return native;
    }

    private void Add(GoldenVector vector)
    {
        if (_file.Length == 0)
            throw new InvalidOperationException("No file started.");
        if (!IdPattern().IsMatch(vector.Id))
            throw Fail(vector.Id, "ids are lower case letters, digits, '.', '-' and '_'");
        if (!_ids.Add(vector.Id))
            throw Fail(vector.Id, "duplicate id");
        _vectors.Add(vector);
    }

    private static void Require(string id, bool condition, string message)
    {
        if (!condition)
            throw Fail(id, message);
    }

    private static InvalidOperationException Fail(string id, string message) => new("Golden vector '" + id + "': " + message + ".");

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]*$")]
    private static partial Regex IdPattern();
}
