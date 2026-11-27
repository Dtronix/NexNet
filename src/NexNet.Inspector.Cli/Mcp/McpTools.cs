using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;

namespace NexNet.Inspector.Cli;

/// <summary>The tools of <c>nexnet-tap mcp</c>, in the order <c>tools/list</c> returns them.</summary>
internal enum McpToolKind : byte
{
    ListSessions,
    Attach,
    Read,
    Detach,
    Summary,
    Findings,
    Show,
    Trace,
    Around,
    Schema,
    Diff,
    ExportSection,
    RecordStart,
    RecordStop,
}

/// <summary>One MCP tool: its name, description, JSON Schema input and effect hints.</summary>
/// <param name="Kind">The tool.</param>
/// <param name="Name">What <c>tools/call</c> names.</param>
/// <param name="Title">Display name.</param>
/// <param name="Description">What the model reads to pick it.</param>
/// <param name="SchemaJson">The input schema (JSON Schema object).</param>
/// <param name="ReadOnly">True when it changes nothing (no file written, nothing started).</param>
internal sealed record McpToolSpec(McpToolKind Kind, string Name, string Title, string Description, string SchemaJson, bool ReadOnly)
{
    /// <summary>The parsed <see cref="SchemaJson"/>.</summary>
    public JsonElement Schema { get; } = Parse(SchemaJson);

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}

/// <summary>
/// The tool table (<see cref="All"/>), argument validation against each tool's schema, and typed argument reads
/// (<see cref="McpArgs"/>). The tools run as <see cref="McpToolRunner"/>.
/// </summary>
internal static class McpTools
{
    /// <summary>Most lines one <c>read</c> returns.</summary>
    public const int MaxReadLines = 1000;

    /// <summary>Lines a <c>read</c> returns when <c>max_lines</c> is not given.</summary>
    public const int DefaultReadLines = 200;

    /// <summary>Most milliseconds a <c>read</c> waits for new lines.</summary>
    public const int MaxWaitMs = 30_000;

    // ----- Schema fragments -----

    private const string Connect = """
        "endpoint": {"type": "string", "description": "Inspector endpoint: tcp://host:port, tls://host:port or uds:/path"},
        "token": {"type": "string", "description": "Endpoint token. Default: NEXNET_TAP_TOKEN in the MCP server's environment. Never echoed"},
        "insecure": {"type": "boolean", "description": "tls:// only: accept any server certificate (self-signed development certificates)"},
        "server_name": {"type": "string", "description": "tls:// only: the name the server certificate must carry (default: the host)"}
        """;

    private const string Source = """
        "file": {"type": "string", "description": "A .nntl recording to read (give file, or endpoint for a live session)"},
        "session": {"type": ["string", "integer"], "description": "A session ID (decimal, or @0xHEX as the lines show it) or 'first'. A file: default every session; live: default the first"},
        "duration": {"type": ["string", "number"], "description": "Live only: how long to attach before querying what arrived (250ms, 2s, 1m; a number is seconds; default 2s)"},
        """ + Connect;

    private const string Output = """
        "format": {"type": "string", "enum": ["text", "ndjson"], "description": "text (default): token-condensed lines; ndjson: one JSON object per line, short keys"},
        "max_lines": {"type": "integer", "minimum": 1, "maximum": 5000, "description": "Stop after this many lines with a '… budget hit, N withheld, --after <cursor>' line"},
        "max_tokens": {"type": "integer", "minimum": 1, "maximum": 50000, "description": "Stop before this many approximate tokens (default 8000 when neither budget is given)"},
        "after": {"type": "string", "description": "Resume the same query: the <cursor> of a '… budget hit' line"},
        "value_width": {"type": "integer", "minimum": 4, "maximum": 4096, "description": "Cut each value at this many characters (default 32; show/trace 64)"},
        "depth": {"type": "integer", "minimum": 0, "maximum": 64, "description": "Nesting shown inside values (default 2; show/trace 8)"}
        """;

    private const string Ids = "IDs as the lines print them: #5 (invocation), 5.2 (2nd use of wire ID 5), ≋4·4 or p4 (pipe), f18 (frame).";

    /// <summary>Every tool, in <see cref="McpToolKind"/> order.</summary>
    public static readonly McpToolSpec[] All =
    [
        new(McpToolKind.ListSessions, "list_sessions", "List live sessions",
            "Connects to a NexNet inspector endpoint and lists what it inspects: role, recording policy (whether record_start is allowed), "
            + "secrets policy, schema hash, then one row per live session (ID, connected ID, state, transport, endpoints, start time).",
            Object(Connect, "endpoint"), ReadOnly: true),
        new(McpToolKind.Attach, "attach", "Attach to a live session",
            "Starts a live tail of a session as token-condensed text lines (one per message; pings and repeats collapsed) and returns a "
            + "stream_id. Page through the lines with read(stream_id, after); end with detach(stream_id). Filters keep only matching "
            + "lines (the header and END lines always pass). session 'all' interleaves every session (needs recording allowed).",
            Object("""
                "session": {"type": ["string", "integer"], "description": "A live session ID from list_sessions, 'first' (waits for one), or 'all'"},
                "level": {"type": "string", "enum": ["summary", "messages", "frames", "bytes"], "description": "Detail: messages (default), frames, bytes, or summary"},
                "format": {"type": "string", "enum": ["text", "ndjson"], "description": "text (default) or ndjson"},
                "collapse": {"type": "boolean", "description": "Collapse pings, credits, windows and repeated lines (default true)"},
                "value_width": {"type": "integer", "minimum": 4, "maximum": 4096, "description": "Cut each value at this many characters (default 32)"},
                "depth": {"type": "integer", "minimum": 0, "maximum": 64, "description": "Nesting shown inside a value (default 2)"},
                "method": {"type": ["string", "array"], "items": {"type": "string"}, "description": "Only calls of these methods: names or globs (Send*, Join)"},
                "pipe": {"type": ["string", "array"], "items": {"type": "string"}, "description": "Only lines of these pipes: 4.4, ≋4·4 or 4"},
                "dir": {"type": "string", "enum": ["in", "out"], "description": "One direction, seen from the tapped side"},
                "only": {"type": ["string", "array"], "items": {"type": "string", "enum": ["errors", "slow", "cancel", "gaps"]}, "description": "Only errors, slow calls, cancellations or gaps (any of them)"},
                "slow": {"type": ["string", "number"], "description": "Slow-call threshold (100ms default; a number is seconds); alone it keeps slow calls only"},
                "grep": {"type": "string", "description": "Only lines containing this text (case-insensitive)"},
                "findings": {"type": "boolean", "description": "Also emit the findings detectors' '!' lines as they are detected"},
                "until": {"type": "string", "description": "Hold lines until one meets this (method=Send*,state=exception|gap), then emit the context before it and stop"},
                "context": {"type": "integer", "minimum": 0, "maximum": 1000, "description": "Lines kept before the until line (default 10)"},
                "duration": {"type": ["string", "number"], "description": "Stop the tail after this long (30s, 5m; a number is seconds)"},
                "wait": {"type": ["string", "number"], "description": "session 'first' only: how long to wait for a session (default 30s)"},
                """ + Connect, "endpoint", "session"), ReadOnly: false),
        new(McpToolKind.Read, "read", "Read a stream",
            "Returns the lines an attached stream has produced since a cursor (after = the line number to start at; 0 or absent = the "
            + $"first line), at most max_lines (default {DefaultReadLines}, hard cap {MaxReadLines}), then a '… next: after=N' line "
            + "with the stream's state. wait_ms long-polls for new lines while the stream runs. Streams keep their last 10000 lines.",
            Object($$"""
                "stream_id": {"type": "string", "description": "From attach"},
                "after": {"type": "integer", "minimum": 0, "description": "The first line wanted: the after=N of the previous read"},
                "max_lines": {"type": "integer", "minimum": 1, "maximum": {{MaxReadLines}}, "description": "At most this many lines (default {{DefaultReadLines}})"},
                "wait_ms": {"type": "integer", "minimum": 0, "maximum": {{MaxWaitMs}}, "description": "Wait up to this long for a new line when none is there yet (default 0)"}
                """, "stream_id"), ReadOnly: true),
        new(McpToolKind.Detach, "detach", "Detach a stream",
            "Ends an attached stream: detaches from the endpoint, and returns the lines not read yet (up to the read cap) and how the "
            + "stream ended. The stream_id is gone afterwards.",
            Object("""
                "stream_id": {"type": "string", "description": "From attach"}
                """, "stream_id"), ReadOnly: false),
        new(McpToolKind.Summary, "summary", "Summarise a capture",
            "About 30 lines per session: totals, handshake, calls per method with p50/p99/max, errors and cancellations, pipes, the "
            + "largest messages, window stalls, gaps, what was open at the end, and the disconnect reason. Reads a recording file, or a "
            + "live session for duration.",
            Object(Source + ",\n" + Output), ReadOnly: true),
        new(McpToolKind.Findings, "findings", "Rank findings",
            "Runs the findings detectors over a recording and returns each session's header line, one '!' line per finding (most "
            + "severe first, citing the IDs to show or trace), and a tally: slow calls, cancellation races, orphan results, window "
            + "and credit stalls, large messages, chunk storms, pending calls, open pipes, protocol errors, schema mismatches, gaps, "
            + "ping outliers; across sessions, reconnect loops.",
            Object("""
                "file": {"type": "string", "description": "A .nntl recording to read"},
                "session": {"type": ["string", "integer"], "description": "A session ID (decimal or @0xHEX), 'first', or 'all' (default)"},
                "slow": {"type": ["string", "number"], "description": "A completed call at least this long is slow (default 1s; a number is seconds)"},
                "format": {"type": "string", "enum": ["text", "ndjson"], "description": "text (default) or ndjson"}
                """, "file"), ReadOnly: true),
        new(McpToolKind.Show, "show", "Show an entity",
            "One invocation, pipe or frame as a compact tree: its line, method, state, arguments and result (or exception) decoded "
            + "field by field, frames and pipe. " + Ids,
            Object("""
                "id": {"type": "string", "description": "#5, 5.2, ≋4·4, p4 or f18"},
                "bytes": {"type": "boolean", "description": "Add each field's byte range (@offset+size) and a frame's hex"},
                """ + Source + ",\n" + Output, "id"), ReadOnly: true),
        new(McpToolKind.Trace, "trace", "Trace an entity",
            "For each message of an entity: the frames that carried it, the bytes each carried, the transport reads or writes they "
            + "arrived in, and the frames interleaved with it on the wire. " + Ids,
            Object("""
                "id": {"type": "string", "description": "#5, 5.2, ≋4·4, p4 or f18"},
                """ + Source + ",\n" + Output, "id"), ReadOnly: true),
        new(McpToolKind.Around, "around", "What happened around a time",
            "The lines within window (default ±25ms) of a time t in ms since the session's start, as the lines print it (+27.5).",
            Object("""
                "t": {"type": ["string", "number"], "description": "The time: +27.5, 27.5, 1.2s (a number is ms)"},
                "window": {"type": ["string", "number"], "description": "Half-width: ±10, 10ms, 1s (a number is ms; default 25)"},
                "level": {"type": "string", "enum": ["messages", "frames", "bytes"], "description": "messages (default), frames or bytes"},
                """ + Source + ",\n" + Output, "t"), ReadOnly: true),
        new(McpToolKind.Schema, "schema", "Show the schema",
            "The session's schema as compact signatures with wire IDs: server methods, collections, client methods, and the declared types.",
            Object(Source + ",\n" + Output), ReadOnly: true),
        new(McpToolKind.Diff, "diff", "Compare two recordings",
            "Compares a session of each recording as the app's Compare does: every entity whose sides differ and every gap, worst "
            + "first, then the field diffs of the top worst entities both sides hold (~ changed, + only in b, - only in a).",
            Object("""
                "a": {"type": "string", "description": "The first .nntl recording"},
                "b": {"type": "string", "description": "The second .nntl recording"},
                "session": {"type": ["string", "integer"], "description": "The session ID to compare in both (default: the first of each)"},
                "top": {"type": "integer", "minimum": 1, "maximum": 100, "description": "Entities whose field diffs are listed (default 5)"},
                """ + Output, "a", "b"), ReadOnly: true),
        new(McpToolKind.ExportSection, "export_section", "Export a time section",
            "Writes a section of one session to a new .nntl that opens on its own (handshake, schema, a snapshot of what was open at "
            + "from, and the frames between from and to). An existing out file is kept unless force. A live export needs the server "
            + "to allow recording.",
            Object("""
                "out": {"type": "string", "description": "The .nntl file to write"},
                "from": {"type": ["string", "number"], "description": "Section start in ms since the session's start (+27.5, 1.2s; default the start)"},
                "to": {"type": ["string", "number"], "description": "Section end (default the end)"},
                "force": {"type": "boolean", "description": "Overwrite out"},
                """ + Source, "out"), ReadOnly: false),
        new(McpToolKind.RecordStart, "record_start", "Start recording",
            "Starts recording a live session (or 'all') verbatim into a .nntl file and returns a record_id; record_stop(record_id) "
            + "ends it and reports what was written. Only when the server's configuration allows recording (list_sessions shows "
            + "the policy); auth tokens stay redacted unless the server includes secrets.",
            Object("""
                "session": {"type": ["string", "integer"], "description": "A live session ID, 'first' (waits for one), or 'all' (new sessions from their first byte)"},
                "out": {"type": "string", "description": "The .nntl file to write; an existing file is kept unless force"},
                "force": {"type": "boolean", "description": "Overwrite out"},
                "duration": {"type": ["string", "number"], "description": "Stop on its own after this long (30s, 5m; a number is seconds)"},
                "overflow": {"type": "string", "enum": ["drop", "block"], "description": "drop (default): record a gap when the recorder falls behind; block: lossless but slows the connection"},
                "snapshot": {"type": "boolean", "description": "Start an attached session with a state snapshot (default true)"},
                "wait": {"type": ["string", "number"], "description": "session 'first' only: how long to wait for a session (default 30s)"},
                """ + Connect, "endpoint", "session", "out"), ReadOnly: false),
        new(McpToolKind.RecordStop, "record_stop", "Stop recording",
            "Stops a recording started by record_start: detaches, waits for the close blocks and the index footer, and reports the "
            + "bytes, sessions and blocks written.",
            Object("""
                "record_id": {"type": "string", "description": "From record_start"}
                """, "record_id"), ReadOnly: false),
    ];

    /// <summary>The tool a <c>tools/call</c> names.</summary>
    public static bool TryGet(string name, [NotNullWhen(true)] out McpToolSpec? spec)
    {
        foreach (var tool in All)
        {
            if (tool.Name == name)
            {
                spec = tool;
                return true;
            }
        }

        spec = null;
        return false;
    }

    private static string Object(string properties, params string[] required)
    {
        var list = required.Length == 0 ? "" : ", \"required\": [\"" + string.Join("\", \"", required) + "\"]";
        return "{\"type\": \"object\", \"properties\": {" + properties + "}" + list + ", \"additionalProperties\": false}";
    }

    /// <summary>
    /// Checks a call's arguments against the tool's schema (the subset the schemas use: type, items, enum, minimum,
    /// maximum, required, no other properties). Returns why they do not fit, worded for the model to fix the call; never
    /// echoes a value.
    /// </summary>
    public static bool TryValidate(McpToolSpec tool, JsonElement arguments, [NotNullWhen(false)] out string? error)
    {
        error = null;
        var properties = tool.Schema.GetProperty("properties");
        if (arguments.ValueKind is not (JsonValueKind.Object or JsonValueKind.Undefined or JsonValueKind.Null))
        {
            error = "arguments must be an object.";
            return false;
        }

        if (arguments.ValueKind == JsonValueKind.Object)
        {
            foreach (var argument in arguments.EnumerateObject())
            {
                if (!properties.TryGetProperty(argument.Name, out var property))
                {
                    error = $"Unknown argument '{argument.Name}'. {tool.Name} takes: {Names(properties)}.";
                    return false;
                }

                if (!Fits(argument.Name, property, argument.Value, out error))
                    return false;
            }
        }

        if (tool.Schema.TryGetProperty("required", out var required))
        {
            foreach (var name in required.EnumerateArray())
            {
                var key = name.GetString()!;
                if (arguments.ValueKind != JsonValueKind.Object || !arguments.TryGetProperty(key, out var value) || value.ValueKind == JsonValueKind.Null)
                {
                    error = $"Missing required argument '{key}'.";
                    return false;
                }
            }
        }

        return true;
    }

    private static string Names(JsonElement properties)
    {
        var names = new List<string>();
        foreach (var p in properties.EnumerateObject())
            names.Add(p.Name);
        return string.Join(", ", names);
    }

    private static bool Fits(string name, JsonElement schema, JsonElement value, [NotNullWhen(false)] out string? error)
    {
        error = null;
        if (value.ValueKind == JsonValueKind.Null)
            return true;
        var type = schema.GetProperty("type");
        var fits = false;
        if (type.ValueKind == JsonValueKind.Array)
        {
            foreach (var t in type.EnumerateArray())
                fits |= IsType(t.GetString()!, value);
        }
        else
        {
            fits = IsType(type.GetString()!, value);
        }

        if (!fits)
        {
            error = $"Argument '{name}' must be {Describe(type)}, not {Kind(value)}.";
            return false;
        }

        if (value.ValueKind == JsonValueKind.Array)
        {
            var items = schema.GetProperty("items");
            foreach (var item in value.EnumerateArray())
            {
                if (!Fits(name, items, item, out error))
                    return false;
            }

            return true;
        }

        if (schema.TryGetProperty("enum", out var allowed) && value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString();
            var names = new List<string>();
            foreach (var option in allowed.EnumerateArray())
            {
                if (option.GetString() == text)
                    return true;
                names.Add(option.GetString()!);
            }

            error = $"Argument '{name}' must be one of {string.Join(", ", names)}.";
            return false;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number))
        {
            if (schema.TryGetProperty("minimum", out var min) && number < min.GetDouble())
            {
                error = string.Create(CultureInfo.InvariantCulture, $"Argument '{name}' must be at least {min.GetDouble()}.");
                return false;
            }

            if (schema.TryGetProperty("maximum", out var max) && number > max.GetDouble())
            {
                error = string.Create(CultureInfo.InvariantCulture, $"Argument '{name}' must be at most {max.GetDouble()}.");
                return false;
            }
        }

        return true;
    }

    private static bool IsType(string type, JsonElement value) => type switch
    {
        "string" => value.ValueKind == JsonValueKind.String,
        "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
        "number" => value.ValueKind == JsonValueKind.Number,
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "array" => value.ValueKind == JsonValueKind.Array,
        _ => false,
    };

    private static string Describe(JsonElement type)
    {
        if (type.ValueKind != JsonValueKind.Array)
            return Article(type.GetString()!);
        var names = new List<string>();
        foreach (var t in type.EnumerateArray())
            names.Add(Article(t.GetString()!));
        return string.Join(" or ", names);
    }

    private static string Article(string type) => type switch
    {
        "integer" => "an integer",
        "array" => "an array of strings",
        _ => "a " + type,
    };

    private static string Kind(JsonValueKind kind) => kind switch
    {
        JsonValueKind.String => "a string",
        JsonValueKind.Number => "a number",
        JsonValueKind.True or JsonValueKind.False => "a boolean",
        JsonValueKind.Array => "an array",
        JsonValueKind.Object => "an object",
        _ => "null",
    };

    private static string Kind(JsonElement value) =>
        value.ValueKind == JsonValueKind.Number && !value.TryGetInt64(out _) ? "a fractional number" : Kind(value.ValueKind);
}

/// <summary>A validated <c>tools/call</c> argument object, read by name.</summary>
internal readonly struct McpArgs(JsonElement arguments)
{
    public bool Has(string name) =>
        arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null;

    /// <summary>A string argument; a number is given as its invariant text.</summary>
    public string? String(string name)
    {
        if (!TryGet(name, out var value))
            return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText();
    }

    public int? Int(string name) => TryGet(name, out var value) && value.TryGetInt32(out var number) ? number : null;

    public bool? Bool(string name) => TryGet(name, out var value) ? value.ValueKind == JsonValueKind.True : null;

    /// <summary>A string or an array of strings, as one comma-separated list.</summary>
    public string? List(string name)
    {
        if (!TryGet(name, out var value))
            return null;
        if (value.ValueKind != JsonValueKind.Array)
            return value.GetString();
        var parts = new List<string>();
        foreach (var item in value.EnumerateArray())
            parts.Add(item.GetString()!);
        return string.Join(',', parts);
    }

    private bool TryGet(string name, out JsonElement value)
    {
        value = default;
        return arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty(name, out value) && value.ValueKind != JsonValueKind.Null;
    }
}
