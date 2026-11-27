using System.Text.Json;
using System.Text.Json.Serialization;

namespace NexNet.Inspector.Cli;

// The MCP result shapes nexnet-tap mcp writes, serialized through the source-generated McpJsonContext (no
// reflection: Native AOT). Requests are read with JsonDocument; envelopes are written by McpServer.

/// <summary>An MCP implementation's identity (`serverInfo`).</summary>
internal sealed record McpImplementation(string Name, string Version, string? Title);

/// <summary>The `tools` capability: the tool list never changes while the server runs.</summary>
internal sealed record McpToolsCapability(bool? ListChanged);

/// <summary>Server capabilities: tools only.</summary>
internal sealed record McpCapabilities(McpToolsCapability Tools);

/// <summary>A result's `_meta` (2026-07-28: the server's identity on each result).</summary>
internal sealed record McpResultMeta([property: JsonPropertyName("io.modelcontextprotocol/serverInfo")] McpImplementation ServerInfo);

/// <summary>The `initialize` result (2025-11-25 and earlier).</summary>
internal sealed record McpInitializeResult(string ProtocolVersion, McpCapabilities Capabilities, McpImplementation ServerInfo, string Instructions);

/// <summary>The `server/discover` result (2026-07-28).</summary>
internal sealed record McpDiscoverResult(
    string ResultType,
    string[] SupportedVersions,
    McpCapabilities Capabilities,
    string Instructions,
    long TtlMs,
    string CacheScope,
    [property: JsonPropertyName("_meta")] McpResultMeta Meta);

/// <summary>Hints about a tool's effects.</summary>
internal sealed record McpToolAnnotations(bool ReadOnlyHint, bool DestructiveHint, bool IdempotentHint, bool OpenWorldHint);

/// <summary>One entry of `tools/list`.</summary>
internal sealed record McpToolInfo(string Name, string Title, string Description, JsonElement InputSchema, McpToolAnnotations Annotations);

/// <summary>The `tools/list` result; the 2026-07-28 fields are null for a legacy client.</summary>
internal sealed record McpToolsListResult(
    McpToolInfo[] Tools,
    string? ResultType,
    long? TtlMs,
    string? CacheScope,
    [property: JsonPropertyName("_meta")] McpResultMeta? Meta);

/// <summary>A text content block.</summary>
internal sealed record McpContent(string Type, string Text);

/// <summary>The `tools/call` result.</summary>
internal sealed record McpCallToolResult(
    McpContent[] Content,
    bool IsError,
    string? ResultType,
    [property: JsonPropertyName("_meta")] McpResultMeta? Meta);

/// <summary>A JSON-RPC error object.</summary>
internal sealed record McpError(int Code, string Message, McpErrorData? Data);

/// <summary>The `data` of an unsupported-protocol-version error.</summary>
internal sealed record McpErrorData(string[] Supported, string? Requested);

/// <summary>Source-generated System.Text.Json metadata for the MCP server.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(McpInitializeResult))]
[JsonSerializable(typeof(McpDiscoverResult))]
[JsonSerializable(typeof(McpToolsListResult))]
[JsonSerializable(typeof(McpCallToolResult))]
[JsonSerializable(typeof(McpError))]
internal sealed partial class McpJsonContext : JsonSerializerContext;
