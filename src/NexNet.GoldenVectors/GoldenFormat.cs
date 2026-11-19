using System.Buffers;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexNet.Serialization;

namespace NexNet.GoldenVectors;

/// <summary>What a runtime does with a vector (tests/golden/README.md, "Modes").</summary>
public enum GoldenMode
{
    /// <summary>Encode <c>value</c> and compare with <c>hex</c>; decode <c>hex</c> and compare with <c>value</c>.</summary>
    Both,

    /// <summary>Decode <c>hex</c> (accepted, non-canonical input) and compare with <c>value</c>.</summary>
    Decode,

    /// <summary>Decoding <c>hex</c> must fail.</summary>
    Reject,
}

/// <summary>Serializer options of a vector; unset members keep the defaults of <see cref="Security"/>.</summary>
/// <param name="Security"><c>untrusted</c> (the default) or <c>trusted</c>.</param>
/// <param name="MaxDepth">Maximum container depth.</param>
/// <param name="StrictUtf8">Whether invalid UTF-8 is rejected.</param>
/// <param name="MaxElements">Most array elements and map entries per value; 0 is no limit.</param>
public sealed record GoldenOptions(string? Security = null, int? MaxDepth = null, bool? StrictUtf8 = null, int? MaxElements = null)
{
    /// <summary>The NexNet serializer options.</summary>
    public NexusSerializerOptions ToSerializerOptions()
    {
        var options = Security switch
        {
            null or "untrusted" => NexusSerializerOptions.Untrusted,
            "trusted" => NexusSerializerOptions.Trusted,
            _ => throw new FormatException("Unknown security '" + Security + "'."),
        };
        if (MaxDepth is { } maxDepth)
            options = options with { MaxDepth = maxDepth };
        if (StrictUtf8 is { } strict)
            options = options with { StrictUtf8 = strict };
        if (MaxElements is { } maxElements)
            options = options with { MaxElements = maxElements };
        return options;
    }

    internal JsonObject ToJson()
    {
        var json = new JsonObject();
        if (Security != null)
            json["security"] = Security;
        if (MaxDepth != null)
            json["maxDepth"] = MaxDepth;
        if (StrictUtf8 != null)
            json["strictUtf8"] = StrictUtf8;
        if (MaxElements != null)
            json["maxElements"] = MaxElements;
        return json;
    }

    internal static GoldenOptions FromJson(JsonObject json)
    {
        foreach (var property in json)
        {
            if (property.Key is not ("security" or "maxDepth" or "strictUtf8" or "maxElements"))
                throw new FormatException("Unknown option '" + property.Key + "'.");
        }

        return new GoldenOptions((string?)json["security"], (int?)json["maxDepth"], (bool?)json["strictUtf8"], (int?)json["maxElements"]);
    }
}

/// <summary>One golden vector: one line of a <c>tests/golden/*.jsonl</c> file.</summary>
/// <param name="File">The file it belongs to, without extension.</param>
/// <param name="Id">Unique id across all files.</param>
/// <param name="Type">The type expression (<see cref="TypeExpr"/>), <c>frame</c> or <c>preamble</c>.</param>
/// <param name="Mode">What a runtime does with it.</param>
/// <param name="Value">The typed-JSON value; unused for <see cref="GoldenMode.Reject"/>.</param>
/// <param name="Hex">The MessagePack (or frame) bytes.</param>
/// <param name="Options">Serializer options, when not the defaults.</param>
/// <param name="Error">Reject only: the kind of error.</param>
/// <param name="Note">A short description.</param>
public sealed record GoldenVector(
    string File,
    string Id,
    string Type,
    GoldenMode Mode,
    JsonNode? Value,
    byte[] Hex,
    GoldenOptions? Options = null,
    string? Error = null,
    string? Note = null)
{
    /// <summary>The serializer options to decode with.</summary>
    public NexusSerializerOptions SerializerOptions => Options?.ToSerializerOptions() ?? NexusSerializerOptions.Untrusted;
}

/// <summary>Reads and writes golden vector lines.</summary>
public static class GoldenFormat
{
    /// <summary>The error kinds of reject vectors.</summary>
    public static readonly IReadOnlyList<string> ErrorKinds =
        ["truncated", "type", "reserved", "range", "count", "size", "length", "elements", "depth", "utf8", "value", "trailing"];

    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Indented = false,
    };

    /// <summary>The canonical JSON text of a typed-JSON value, for comparisons.</summary>
    public static string Canonical(JsonNode? value)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            if (value is null)
                writer.WriteNullValue();
            else
                value.WriteTo(writer);
        }

        return System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>One line, without the line break.</summary>
    public static string Write(GoldenVector v)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer, WriterOptions))
        {
            w.WriteStartObject();
            w.WriteString("id", v.Id);
            w.WriteString("type", v.Type);
            w.WriteString("mode", ModeName(v.Mode));
            if (v.Options != null)
            {
                w.WritePropertyName("options");
                v.Options.ToJson().WriteTo(w);
            }

            if (v.Error != null)
                w.WriteString("error", v.Error);
            if (v.Note != null)
                w.WriteString("note", v.Note);
            if (v.Mode != GoldenMode.Reject)
            {
                w.WritePropertyName("value");
                if (v.Value is null)
                    w.WriteNullValue();
                else
                    v.Value.WriteTo(w);
            }

            w.WriteString("hex", Convert.ToHexStringLower(v.Hex));
            w.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>Parses one line.</summary>
    /// <exception cref="FormatException">The line is malformed.</exception>
    public static GoldenVector Read(string file, string line)
    {
        var json = JsonNode.Parse(line) as JsonObject ?? throw new FormatException("A vector must be a JSON object.");
        foreach (var property in json)
        {
            if (property.Key is not ("id" or "type" or "mode" or "options" or "error" or "note" or "value" or "hex"))
                throw new FormatException("Unknown vector field '" + property.Key + "'.");
        }

        var id = (string?)json["id"] ?? throw new FormatException("A vector needs an id.");
        var mode = (string?)json["mode"] switch
        {
            "both" => GoldenMode.Both,
            "decode" => GoldenMode.Decode,
            "reject" => GoldenMode.Reject,
            var other => throw new FormatException("Vector '" + id + "' has an unknown mode '" + other + "'."),
        };
        if (mode != GoldenMode.Reject && !json.ContainsKey("value"))
            throw new FormatException("Vector '" + id + "' needs a value.");
        var error = (string?)json["error"];
        if (error != null && (mode != GoldenMode.Reject || !ErrorKinds.Contains(error)))
            throw new FormatException("Vector '" + id + "' has an invalid error '" + error + "'.");

        return new GoldenVector(
            file,
            id,
            (string?)json["type"] ?? throw new FormatException("Vector '" + id + "' needs a type."),
            mode,
            json["value"]?.DeepClone(),
            Convert.FromHexString((string?)json["hex"] ?? throw new FormatException("Vector '" + id + "' needs hex.")),
            json["options"] is JsonObject options ? GoldenOptions.FromJson(options) : null,
            error,
            (string?)json["note"]);
    }

    /// <summary>The name of a mode in the files.</summary>
    public static string ModeName(GoldenMode mode) => mode switch
    {
        GoldenMode.Both => "both",
        GoldenMode.Decode => "decode",
        GoldenMode.Reject => "reject",
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };
}
