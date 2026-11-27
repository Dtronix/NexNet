using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using NexNet.Inspector.Core.Model;

namespace NexNet.Inspector.Core.Text;

/// <summary>
/// Writes <see cref="TextRecord"/>s as text lines or ndjson objects (docs/internals/inspector-text-format.md). One
/// writer per renderer: it reuses its line buffer, JSON writer and line object.
/// </summary>
public sealed class TextRecordWriter
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    private static readonly JsonWriterOptions JsonOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly TextOptions _options;
    private readonly StringBuilder _line = new();
    private readonly ArrayBufferWriter<byte> _json = new(256);
    private readonly TextJsonLine _object = new();
    private Utf8JsonWriter? _jsonWriter;
    private char[] _chars = new char[256];
    private int _charCount;

    /// <summary>Creates a writer.</summary>
    /// <param name="options">The format to write.</param>
    public TextRecordWriter(TextOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    /// <summary>The line last formatted by <see cref="Measure"/>.</summary>
    public string Current => _options.Format == TextFormat.Ndjson ? new string(_chars, 0, _charCount) : _line.ToString();

    /// <summary>Formats a record into the writer's buffer and returns its approximate tokens.</summary>
    /// <param name="record">The record.</param>
    public long Measure(in TextRecord record)
    {
        _line.Clear();
        if (_options.Format == TextFormat.Ndjson)
        {
            FillJson(record);
            SerializeJson();
            return TextBudget.Estimate(_chars.AsSpan(0, _charCount));
        }

        AppendText(_line, record);
        return TextBudget.Estimate(_line);
    }

    /// <summary>Writes the formatted line and a newline.</summary>
    /// <param name="output">The output.</param>
    public void Write(TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (_options.Format == TextFormat.Ndjson)
        {
            output.Write(_chars.AsSpan(0, _charCount));
        }
        else
        {
            foreach (var chunk in _line.GetChunks())
                output.Write(chunk.Span);
        }

        output.Write('\n');
    }

    /// <summary>Writes the budget line: `… budget hit, N withheld, --after &lt;cursor&gt;`.</summary>
    /// <param name="output">The output.</param>
    /// <param name="withheld">Lines held back.</param>
    /// <param name="cursor">Where to resume.</param>
    public void WriteBudget(TextWriter output, int withheld, TextCursor cursor)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (_options.Format == TextFormat.Ndjson)
        {
            _object.Clear();
            _object.K = "budget";
            _object.X = withheld;
            _object.After = cursor.Encode();
            SerializeJson();
            output.Write(_chars.AsSpan(0, _charCount));
            output.Write('\n');
            return;
        }

        output.Write(string.Create(Invariant, $"… budget hit, {withheld} withheld, --after {cursor.Encode()}\n"));
    }

    /// <summary>The text form of a record (docs/internals/inspector-text-format.md §3).</summary>
    /// <param name="sb">Receives the line, without newline.</param>
    /// <param name="record">The record.</param>
    public static void AppendText(StringBuilder sb, in TextRecord record)
    {
        ArgumentNullException.ThrowIfNull(sb);
        switch (record.Kind)
        {
            case TextRecordKind.Header:
                sb.Append(record.Id).Append(' ').Append(record.Value);
                return;
            case TextRecordKind.Handshake:
                sb.Append("hs ").Append(record.Outcome);
                if (record.Value is { Length: > 0 })
                    sb.Append(' ').Append(record.Value);
                return;
            case TextRecordKind.Stat:
                sb.Append(record.Value);
                return;
            case TextRecordKind.Bytes:
                sb.Append("  ").Append(record.Value);
                return;
        }

        TextValues.AppendTime(sb, record.TimeMs).Append(' ');
        switch (record.Kind)
        {
            case TextRecordKind.Call:
                Arrow(sb, record).Append(record.Id);
                if (record.Count > 1 && record.LastId is not null)
                    sb.Append("..").Append(record.LastId);
                sb.Append(' ').Append(record.Name).Append('(').Append(record.Value).Append(')');
                Outcome(sb, record);
                Collapsed(sb, record);
                return;
            case TextRecordKind.Result:
                Arrow(sb, record).Append(record.Id).Append(' ').Append(record.Name);
                Outcome(sb, record);
                return;
            case TextRecordKind.Cancel:
                Arrow(sb, record).Append(record.Id).Append(" cancel");
                return;
            case TextRecordKind.Item:
            case TextRecordKind.Collection:
                Arrow(sb, record).Append(record.Id);
                if (record.Name is not null)
                    sb.Append(' ').Append(record.Name);
                if (record.Count > 1)
                    sb.Append(Invariant, $" ×{record.Count}");
                if (record.Value is { Length: > 0 })
                    sb.Append(' ').Append(record.Value);
                if (record.Count > 1)
                    sb.Append(Invariant, $" (…{record.Count - 1} more)");
                return;
            case TextRecordKind.Data:
                Arrow(sb, record).Append(record.Id).Append(" data");
                if (record.Count > 1)
                    sb.Append(Invariant, $" ×{record.Count}");
                TextValues.AppendSize(sb.Append(' '), Math.Max(0, record.Bytes));
                if (record.Value is { Length: > 0 })
                    sb.Append(' ').Append(record.Value);
                Collapsed(sb, record, count: false);
                return;
            case TextRecordKind.Pipe:
                Arrow(sb, record).Append(record.Id).Append(' ').Append(record.Value);
                return;
            case TextRecordKind.Noise:
                if (record.Count > 1)
                {
                    sb.Append(record.Value);
                    TextValues.AppendDuration(sb.Append(" (span "), record.SpanMs).Append(')');
                }
                else
                {
                    Arrow(sb, record).Append(record.Value);
                }

                return;
            case TextRecordKind.Gap:
                sb.Append("GAP ").Append(record.Direction == TapDirection.Outbound ? "out " : "in ").Append(record.Value).Append(' ');
                TextValues.AppendSize(sb, Math.Max(0, record.Bytes)).Append(" (Drop)");
                return;
            case TextRecordKind.End:
                sb.Append("END ").Append(record.Outcome);
                if (record.Name is not null)
                    sb.Append(" from ").Append(record.Name);
                return;
            case TextRecordKind.Frame:
                Arrow(sb, record).Append(record.Id).Append(' ').Append(record.Name);
                if (record.Value is { Length: > 0 })
                    sb.Append(' ').Append(record.Value);
                if (record.Bytes >= 0)
                    TextValues.AppendSize(sb.Append(' '), record.Bytes);
                if (record.Outcome is { Length: > 0 })
                    sb.Append(' ').Append(record.Outcome);
                return;
            case TextRecordKind.Problem:
                sb.Append("! ").Append(record.Id).Append(' ').Append(record.Value);
                return;
            default:
                sb.Append(record.Value);
                return;
        }
    }

    /// <summary>The ndjson `k` code of a kind.</summary>
    public static string Code(TextRecordKind kind) => kind switch
    {
        TextRecordKind.Header => "hdr",
        TextRecordKind.Handshake => "hs",
        TextRecordKind.Call => "call",
        TextRecordKind.Result => "res",
        TextRecordKind.Cancel => "cancel",
        TextRecordKind.Item => "item",
        TextRecordKind.Collection => "coll",
        TextRecordKind.Data => "data",
        TextRecordKind.Pipe => "pipe",
        TextRecordKind.Noise => "noise",
        TextRecordKind.Gap => "gap",
        TextRecordKind.End => "end",
        TextRecordKind.Frame => "frame",
        TextRecordKind.Bytes => "hex",
        TextRecordKind.Stat => "stat",
        _ => "prob",
    };

    private static StringBuilder Arrow(StringBuilder sb, in TextRecord record) => record.Direction switch
    {
        TapDirection.Outbound => sb.Append('>'),
        TapDirection.Inbound => sb.Append('<'),
        _ => sb,
    };

    private static void Outcome(StringBuilder sb, in TextRecord record)
    {
        if (record.Outcome is null)
            return;
        if (record.Outcome == "oneway")
        {
            sb.Append(" (oneway)");
            return;
        }

        sb.Append(" -> ").Append(record.Outcome);
        if (record.Result is { Length: > 0 })
            sb.Append(' ').Append(record.Result);
        if (record.DurationMs is { } duration)
            TextValues.AppendDuration(sb.Append(' '), duration);
    }

    private static void Collapsed(StringBuilder sb, in TextRecord record, bool count = true)
    {
        if (record.Count <= 1)
            return;
        if (count)
            sb.Append(Invariant, $" ×{record.Count}");
        TextValues.AppendDuration(sb.Append(" (span "), record.SpanMs).Append(')');
    }

    private void FillJson(in TextRecord record)
    {
        var o = _object;
        o.Clear();
        o.K = Code(record.Kind);
        if (record.Kind is not (TextRecordKind.Header or TextRecordKind.Stat or TextRecordKind.Bytes))
            o.T = Math.Round(record.TimeMs, 1);
        o.D = record.Direction switch
        {
            TapDirection.Outbound => ">",
            TapDirection.Inbound => "<",
            _ => null,
        };
        o.Id = record.Id;
        o.To = record.Count > 1 ? record.LastId : null;
        o.N = record.Name;
        o.V = record.Value;
        o.R = record.Outcome;
        o.Rv = record.Result;
        o.Ms = record.DurationMs is { } ms ? Math.Round(ms, 1) : null;
        if (record.Count > 1)
        {
            o.X = record.Count;
            o.Sp = Math.Round(record.SpanMs, 1);
        }

        o.B = record.Bytes >= 0 ? record.Bytes : null;
        o.F = record.Frame >= 0 ? record.Frame + 1 : null;
    }

    private void SerializeJson()
    {
        _json.ResetWrittenCount();
        if (_jsonWriter is null)
            _jsonWriter = new Utf8JsonWriter(_json, JsonOptions);
        else
            _jsonWriter.Reset(_json);
        JsonSerializer.Serialize(_jsonWriter, _object, TextJsonContext.Default.TextJsonLine);
        _jsonWriter.Flush();
        var bytes = _json.WrittenSpan;
        var needed = Encoding.UTF8.GetMaxCharCount(bytes.Length);
        if (_chars.Length < needed)
            _chars = new char[Math.Max(needed, _chars.Length * 2)];
        _charCount = Encoding.UTF8.GetChars(bytes, _chars);
    }
}

/// <summary>One ndjson line; the short keys are documented in docs/internals/inspector-text-format.md §4.</summary>
internal sealed class TextJsonLine
{
    [JsonPropertyName("k")]
    public string K { get; set; } = "";

    [JsonPropertyName("t")]
    public double? T { get; set; }

    [JsonPropertyName("d")]
    public string? D { get; set; }

    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("to")]
    public string? To { get; set; }

    [JsonPropertyName("n")]
    public string? N { get; set; }

    [JsonPropertyName("v")]
    public string? V { get; set; }

    [JsonPropertyName("r")]
    public string? R { get; set; }

    [JsonPropertyName("rv")]
    public string? Rv { get; set; }

    [JsonPropertyName("ms")]
    public double? Ms { get; set; }

    [JsonPropertyName("x")]
    public int? X { get; set; }

    [JsonPropertyName("sp")]
    public double? Sp { get; set; }

    [JsonPropertyName("b")]
    public long? B { get; set; }

    [JsonPropertyName("f")]
    public int? F { get; set; }

    [JsonPropertyName("after")]
    public string? After { get; set; }

    public void Clear()
    {
        K = "";
        T = null;
        D = null;
        Id = null;
        To = null;
        N = null;
        V = null;
        R = null;
        Rv = null;
        Ms = null;
        X = null;
        Sp = null;
        B = null;
        F = null;
        After = null;
    }
}

/// <summary>Source-generated JSON for <see cref="TextJsonLine"/> (no reflection, Native AOT).</summary>
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(TextJsonLine))]
internal sealed partial class TextJsonContext : JsonSerializerContext;
