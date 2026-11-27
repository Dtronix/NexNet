using System.Buffers;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using NexNet.Inspector.Core.Format;
using NexNet.Inspector.Core.Text;
using NexNet.Inspector.Tap;

namespace NexNet.Inspector.Cli;

/// <summary>The JSON-RPC methods the server knows.</summary>
internal enum McpMethod : byte
{
    Unknown,
    Initialize,
    Initialized,
    Ping,
    Discover,
    ToolsList,
    ToolsCall,
    Cancelled,
}

/// <summary>
/// <c>nexnet-tap mcp</c>: a Model Context Protocol server over stdio with tools only (<see cref="McpTools"/>), hand-rolled
/// JSON-RPC 2.0: one message per line on stdin and stdout (UTF-8, no embedded newlines), logs on stderr only.
/// </summary>
/// <remarks>
/// <para>
/// Protocol: revision <see cref="ProtocolVersion"/> (stateless: <c>server/discover</c>, every request carries
/// <c>_meta["io.modelcontextprotocol/protocolVersion"]</c> and <c>clientCapabilities</c>, results carry
/// <c>resultType</c>), and, as the spec allows a dual-era server, the legacy flow of 2025-11-25 and earlier:
/// <c>initialize</c> (version negotiation; it selects legacy semantics for the process), <c>notifications/initialized</c>,
/// <c>ping</c>. <c>notifications/cancelled</c> cancels an in-flight <c>tools/call</c>, which then gets no response.
/// </para>
/// <para>
/// Errors: -32700 a line that is not JSON, -32600 not a request (batches included: one message per line), -32601 an
/// unknown method, -32602 bad params (unknown tool, missing protocol version), -32022 an unsupported protocol version,
/// -32603 an internal failure. A tool whose arguments do not fit its schema, or that fails, returns a result with
/// <c>isError</c> and a message the model can act on.
/// </para>
/// </remarks>
internal sealed class McpServer : IAsyncDisposable
{
    /// <summary>The MCP revision targeted (the current stable one on modelcontextprotocol.io, 2026-11).</summary>
    public const string ProtocolVersion = "2026-07-28";

    /// <summary>The newest revision of the initialize-based (legacy) protocol.</summary>
    public const string LegacyProtocolVersion = "2025-11-25";

    /// <summary>Every revision served: the current one, then the legacy ones <c>initialize</c> accepts.</summary>
    public static readonly string[] SupportedVersions = [ProtocolVersion, LegacyProtocolVersion, "2025-06-18", "2025-03-26"];

    public const int ParseError = -32700;
    public const int InvalidRequest = -32600;
    public const int MethodNotFound = -32601;
    public const int InvalidParams = -32602;
    public const int InternalError = -32603;
    public const int UnsupportedProtocolVersion = -32022;

    private const string MetaVersion = "io.modelcontextprotocol/protocolVersion";
    private const string MetaCapabilities = "io.modelcontextprotocol/clientCapabilities";

    private static readonly JsonWriterOptions WriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static readonly McpImplementation Implementation = new("nexnet-tap",
        string.Create(CultureInfo.InvariantCulture, $"{NexusInspector.ApiVersion}.{NntlFormat.Version}.0"), "NexNet Inspector (nexnet-tap)");

    private static readonly McpResultMeta Meta = new(Implementation);

    /// <summary>What the client tells the model about this server.</summary>
    public static readonly string Instructions =
        "nexnet-tap reads NexNet Inspector captures as token-condensed text. A recording (.nntl): summary first, then findings, "
        + "show/trace an ID the lines cite, around a time, schema, diff two recordings, export_section. A live inspector endpoint: "
        + "list_sessions, then attach + read (page with after) + detach for a live tail, summary/show/... with endpoint for a short "
        + "attach, record_start/record_stop to record (when the server allows it). A '… budget hit, N withheld, --after <cursor>' "
        + "line means more: repeat the call with after = <cursor>. Endpoint tokens come from the token argument or NEXNET_TAP_TOKEN "
        + "and are never echoed; auth tokens in captures stay redacted.\n\nText format legend:\n" + TextLegend.Text;

    private readonly TextReader _input;
    private readonly TextWriter _output;
    private readonly TextWriter _log;
    private readonly object _writeGate = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _inflight = new(StringComparer.Ordinal);
    private readonly List<Task> _calls = [];
    private readonly McpToolInfo[] _tools;

    // An initialize request selects legacy semantics for the rest of the process.
    private volatile bool _legacy;

    /// <param name="input">Client to server messages, one per line.</param>
    /// <param name="output">Server to client messages, one per line; nothing else is written to it.</param>
    /// <param name="log">Diagnostics (stderr).</param>
    /// <param name="defaultToken">The endpoint token tools use when a call gives none.</param>
    public McpServer(TextReader input, TextWriter output, TextWriter log, string? defaultToken)
    {
        _input = input;
        _output = output;
        _log = log;
        Tools = new McpToolRunner(defaultToken);
        _tools = new McpToolInfo[McpTools.All.Length];
        for (var i = 0; i < _tools.Length; i++)
        {
            var t = McpTools.All[i];
            _tools[i] = new McpToolInfo(t.Name, t.Title, t.Description, t.Schema,
                new McpToolAnnotations(ReadOnlyHint: t.ReadOnly, DestructiveHint: false, IdempotentHint: t.ReadOnly, OpenWorldHint: false));
        }
    }

    /// <summary>The tools and their background runs.</summary>
    public McpToolRunner Tools { get; }

    /// <summary>The <c>mcp</c> command: serves stdin/stdout until stdin closes or Ctrl+C.</summary>
    public static async Task<ExitCode> RunCommandAsync(TapCli cli, CliArguments args, CancellationToken cancellationToken)
    {
        await cli.Error.WriteLineAsync(
            $"nexnet-tap mcp: serving MCP {ProtocolVersion} (and {LegacyProtocolVersion} via initialize) on stdio; tools only; logs on stderr.")
            .ConfigureAwait(false);
        var server = new McpServer(cli.Input, cli.Output, cli.Error, args.Token);
        await using (server.ConfigureAwait(false))
            await server.RunAsync(cancellationToken).ConfigureAwait(false);
        return ExitCode.Ok;
    }

    /// <summary>Reads and answers messages until the input ends or <paramref name="cancellationToken"/> fires.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && !_shutdown.IsCancellationRequested)
        {
            string? line;
            try
            {
                line = await _input.ReadLineAsync(cancellationToken).AsTask().WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (IOException e)
            {
                Log("input failed: " + e.Message);
                break;
            }

            if (line is null)
                break;
            if (!string.IsNullOrWhiteSpace(line))
                Handle(line);
        }

        // stdin closed: calls in flight still answer (briefly); Ctrl+C cancels them.
        await ShutdownAsync(drain: !cancellationToken.IsCancellationRequested).ConfigureAwait(false);
    }

    /// <summary>Tool calls still running.</summary>
    internal int InFlight => _inflight.Count;

    public async ValueTask DisposeAsync()
    {
        await ShutdownAsync(drain: false).ConfigureAwait(false);
        _shutdown.Dispose();
    }

    private async Task ShutdownAsync(bool drain)
    {
        if (drain)
            await WaitForCallsAsync(DrainTimeout).ConfigureAwait(false);
        if (!_shutdown.IsCancellationRequested)
            await _shutdown.CancelAsync().ConfigureAwait(false);
        await WaitForCallsAsync(DrainTimeout).ConfigureAwait(false);
        await Tools.DisposeAsync().ConfigureAwait(false);
    }

    private async Task WaitForCallsAsync(TimeSpan timeout)
    {
        Task[] calls;
        lock (_calls)
            calls = [.. _calls];
        try
        {
            await Task.WhenAll(calls).WaitAsync(timeout).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // A call that does not end is abandoned with the process.
        }
    }

    /// <summary>How long calls in flight get to finish once stdin closes, and again once they are cancelled.</summary>
    public static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(30);

    // ----- Messages -----

    private void Handle(string line)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            SendError(null, ParseError, "Parse error: each line must be one JSON-RPC message (a JSON object).");
            return;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Array)
            {
                SendError(null, InvalidRequest, "Invalid request: batches are not supported; send one message per line.");
                return;
            }

            if (root.ValueKind != JsonValueKind.Object)
            {
                SendError(null, InvalidRequest, "Invalid request: a message is a JSON object.");
                return;
            }

            JsonElement? id = null;
            if (root.TryGetProperty("id", out var idElement))
            {
                if (idElement.ValueKind is not (JsonValueKind.String or JsonValueKind.Number))
                {
                    SendError(null, InvalidRequest, "Invalid request: id must be a string or a number.");
                    return;
                }

                id = idElement.Clone();
            }

            if (!root.TryGetProperty("jsonrpc", out var version) || version.ValueKind != JsonValueKind.String || version.GetString() != "2.0")
            {
                SendError(id, InvalidRequest, "Invalid request: jsonrpc must be \"2.0\".");
                return;
            }

            if (!root.TryGetProperty("method", out var methodElement) || methodElement.ValueKind != JsonValueKind.String)
            {
                // A response to a request of ours: this server sends none, so there is nothing to match.
                if (root.TryGetProperty("result", out _) || root.TryGetProperty("error", out _))
                    return;
                SendError(id, InvalidRequest, "Invalid request: method must be a string.");
                return;
            }

            var name = methodElement.GetString()!;
            var method = Parse(name);
            if (!root.TryGetProperty("params", out var parameters))
                parameters = default;
            else if (parameters.ValueKind != JsonValueKind.Object)
            {
                if (id is not null)
                    SendError(id, InvalidParams, "Invalid params: params must be an object.");
                return;
            }

            if (id is not { } requestId)
            {
                Notify(method, parameters);
                return;
            }

            Request(requestId, name, method, parameters);
        }
    }

    private static McpMethod Parse(string name) => name switch
    {
        "initialize" => McpMethod.Initialize,
        "notifications/initialized" => McpMethod.Initialized,
        "ping" => McpMethod.Ping,
        "server/discover" => McpMethod.Discover,
        "tools/list" => McpMethod.ToolsList,
        "tools/call" => McpMethod.ToolsCall,
        "notifications/cancelled" => McpMethod.Cancelled,
        _ => McpMethod.Unknown,
    };

    private void Notify(McpMethod method, JsonElement parameters)
    {
        switch (method)
        {
            case McpMethod.Initialized:
                Log("client initialized");
                break;
            case McpMethod.Cancelled:
                if (parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty("requestId", out var target)
                                                                && target.ValueKind is JsonValueKind.String or JsonValueKind.Number
                                                                && _inflight.TryGetValue(Key(target), out var call))
                {
                    try
                    {
                        call.Cancel();
                    }
                    catch (ObjectDisposedException)
                    {
                        // It just finished.
                    }
                }

                break;
        }

        // Other notifications need nothing from a tools-only server.
    }

    private void Request(JsonElement id, string name, McpMethod method, JsonElement parameters)
    {
        switch (method)
        {
            case McpMethod.Initialize:
                Initialize(id, parameters);
                return;
            case McpMethod.Ping:
                SendEmpty(id);
                return;
            case McpMethod.Discover:
                SendResult(id, new McpDiscoverResult("complete", SupportedVersions, new McpCapabilities(new McpToolsCapability(null)),
                    Instructions, 3_600_000, "public", Meta), McpJsonContext.Default.McpDiscoverResult);
                return;
            case McpMethod.ToolsList:
                if (!TryEra(id, parameters, out var modern))
                    return;
                SendResult(id, new McpToolsListResult(_tools, modern ? "complete" : null, modern ? 3_600_000 : null, modern ? "public" : null,
                    modern ? Meta : null), McpJsonContext.Default.McpToolsListResult);
                return;
            case McpMethod.ToolsCall:
                if (!TryEra(id, parameters, out modern))
                    return;
                Call(id, parameters, modern);
                return;
            default:
                SendError(id, MethodNotFound,
                    $"Method not found: {name}. This tools-only server implements initialize, ping, server/discover, tools/list and tools/call.");
                return;
        }
    }

    private void Initialize(JsonElement id, JsonElement parameters)
    {
        var requested = parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty("protocolVersion", out var v)
                                                                    && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
        // Negotiation: the requested legacy revision when served, otherwise the newest legacy one.
        var version = LegacyProtocolVersion;
        for (var i = 1; i < SupportedVersions.Length; i++)
        {
            if (SupportedVersions[i] == requested)
                version = requested;
        }

        _legacy = true;
        Log($"initialize: protocol {version}");
        SendResult(id, new McpInitializeResult(version, new McpCapabilities(new McpToolsCapability(false)), Implementation, Instructions),
            McpJsonContext.Default.McpInitializeResult);
    }

    // A 2026-07-28 request names its revision in _meta; without one, only a process that ran initialize is served.
    private bool TryEra(JsonElement id, JsonElement parameters, out bool modern)
    {
        modern = false;
        if (parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty("_meta", out var meta) && meta.ValueKind == JsonValueKind.Object
            && meta.TryGetProperty(MetaVersion, out var version))
        {
            var requested = version.ValueKind == JsonValueKind.String ? version.GetString() : null;
            if (requested != ProtocolVersion)
            {
                SendError(id, UnsupportedProtocolVersion,
                    $"Unsupported protocol version: send {MetaVersion} \"{ProtocolVersion}\", or call initialize for {LegacyProtocolVersion}.",
                    new McpErrorData([ProtocolVersion], requested));
                return false;
            }

            if (!meta.TryGetProperty(MetaCapabilities, out var capabilities) || capabilities.ValueKind != JsonValueKind.Object)
            {
                SendError(id, InvalidParams, $"Invalid params: _meta[\"{MetaCapabilities}\"] (an object) is required with {MetaVersion}.");
                return false;
            }

            modern = true;
            return true;
        }

        if (_legacy)
            return true;
        SendError(id, InvalidParams,
            $"Invalid params: _meta[\"{MetaVersion}\"] is required ({ProtocolVersion}); or call initialize first ({LegacyProtocolVersion}).");
        return false;
    }

    private void Call(JsonElement id, JsonElement parameters, bool modern)
    {
        if (parameters.ValueKind != JsonValueKind.Object || !parameters.TryGetProperty("name", out var nameElement)
                                                         || nameElement.ValueKind != JsonValueKind.String)
        {
            SendError(id, InvalidParams, "Invalid params: tools/call needs params.name, a tool name from tools/list.");
            return;
        }

        var name = nameElement.GetString()!;
        if (!McpTools.TryGet(name, out var tool))
        {
            SendError(id, InvalidParams, $"Unknown tool: {name}. tools/list names the tools.");
            return;
        }

        if (!parameters.TryGetProperty("arguments", out var arguments))
            arguments = default;
        if (!McpTools.TryValidate(tool, arguments, out var problem))
        {
            SendResult(id, Result(McpToolOutcome.Error(problem + $" (see {name}'s inputSchema in tools/list)"), modern),
                McpJsonContext.Default.McpCallToolResult);
            return;
        }

        var key = Key(id);
        var cancel = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        if (!_inflight.TryAdd(key, cancel))
        {
            cancel.Dispose();
            SendError(id, InvalidRequest, "Invalid request: a request with this id is still running.");
            return;
        }

        var args = new McpArgs(arguments.ValueKind == JsonValueKind.Undefined ? default : arguments.Clone());
        var task = Task.Run(() => RunToolAsync(id, key, tool, args, modern, cancel), CancellationToken.None);
        lock (_calls)
        {
            _calls.RemoveAll(static t => t.IsCompleted);
            _calls.Add(task);
        }
    }

    private async Task RunToolAsync(JsonElement id, string key, McpToolSpec tool, McpArgs args, bool modern, CancellationTokenSource cancel)
    {
        try
        {
            var outcome = await Tools.CallAsync(tool, args, cancel.Token).ConfigureAwait(false);
            if (!cancel.IsCancellationRequested)
                SendResult(id, Result(outcome, modern), McpJsonContext.Default.McpCallToolResult);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            // Cancelled by the client (or shutdown): no response.
        }
        catch (Exception e)
        {
            Log($"{tool.Name} failed: {e.GetType().Name}: {e.Message}");
            SendError(id, InternalError, $"Internal error in {tool.Name}: {e.Message}");
        }
        finally
        {
            _inflight.TryRemove(key, out _);
            cancel.Dispose();
        }
    }

    private static McpCallToolResult Result(McpToolOutcome outcome, bool modern) =>
        new([new McpContent("text", outcome.Text)], outcome.IsError, modern ? "complete" : null, modern ? Meta : null);

    private static string Key(JsonElement id) =>
        id.ValueKind == JsonValueKind.String ? "s:" + id.GetString() : "n:" + id.GetRawText();

    // ----- Output -----

    private void SendResult<T>(JsonElement id, T value, JsonTypeInfo<T> info)
    {
        var buffer = new ArrayBufferWriter<byte>(512);
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            Envelope(writer, id);
            writer.WritePropertyName("result");
            JsonSerializer.Serialize(writer, value, info);
            writer.WriteEndObject();
        }

        Send(buffer.WrittenSpan);
    }

    private void SendEmpty(JsonElement id)
    {
        var buffer = new ArrayBufferWriter<byte>(64);
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            Envelope(writer, id);
            writer.WriteStartObject("result");
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        Send(buffer.WrittenSpan);
    }

    private void SendError(JsonElement? id, int code, string message, McpErrorData? data = null)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            Envelope(writer, id);
            writer.WritePropertyName("error");
            JsonSerializer.Serialize(writer, new McpError(code, message, data), McpJsonContext.Default.McpError);
            writer.WriteEndObject();
        }

        Send(buffer.WrittenSpan);
    }

    private static void Envelope(Utf8JsonWriter writer, JsonElement? id)
    {
        writer.WriteStartObject();
        writer.WriteString("jsonrpc", "2.0");
        writer.WritePropertyName("id");
        if (id is { } value)
            value.WriteTo(writer);
        else
            writer.WriteNullValue();
    }

    // One message per line: the writer escapes control characters, so a message never holds a newline.
    private void Send(ReadOnlySpan<byte> json)
    {
        var text = Encoding.UTF8.GetString(json);
        lock (_writeGate)
        {
            try
            {
                _output.Write(text);
                _output.Write('\n');
                _output.Flush();
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException)
            {
                Log("output failed: " + e.Message);
                _shutdown.Cancel();
            }
        }
    }

    private void Log(string message)
    {
        try
        {
            _log.WriteLine("nexnet-tap mcp: " + message);
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            // No stderr.
        }
    }
}
