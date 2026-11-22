using System.Text.Json.Nodes;

namespace NexNet.Interop.Remote;

/// <summary>The <c>hello</c> message of a language host (control protocol §4.1).</summary>
public sealed record HostHello
{
    /// <summary>The protocol version this runner speaks.</summary>
    public const int ProtocolVersion = 1;

    /// <summary>The protocol version.</summary>
    public int Proto { get; init; }

    /// <summary>The launch token.</summary>
    public string Token { get; init; } = "";

    /// <summary>The language name.</summary>
    public string Lang { get; init; } = "";

    /// <summary>The runtime name and version.</summary>
    public string Runtime { get; init; } = "";

    /// <summary>The client library and its version.</summary>
    public string Lib { get; init; } = "";

    /// <summary>Lower-case hex SHA-256 of the IR the host was generated from.</summary>
    public string SchemaHash { get; init; } = "";

    /// <summary>The capability names the host reported.</summary>
    public IReadOnlyList<string> CapabilityNames { get; init; } = [];

    /// <summary>The transport names the host reported.</summary>
    public IReadOnlyList<string> TransportNames { get; init; } = [];

    /// <summary>How many tests the host runs at once.</summary>
    public int MaxConcurrency { get; init; } = 1;

    /// <summary>The host's process id, if it has one.</summary>
    public int? Pid { get; init; }

    /// <summary>The reported capabilities; unknown names are ignored.</summary>
    public ClientCapabilities Capabilities
    {
        get
        {
            var result = ClientCapabilities.None;
            foreach (var name in CapabilityNames)
            {
                if (Enum.TryParse<ClientCapabilities>(name, ignoreCase: false, out var flag) && Enum.IsDefined(flag))
                    result |= flag;
            }

            return result;
        }
    }

    /// <summary>The reported transports; unknown names are ignored.</summary>
    public IReadOnlyList<ClientTransport> Transports
        => TransportNames
            .Select(n => Enum.TryParse<ClientTransport>(n, ignoreCase: false, out var t) && Enum.IsDefined(t) ? (ClientTransport?)t : null)
            .Where(t => t != null)
            .Select(t => t!.Value)
            .Distinct()
            .ToArray();

    /// <summary>
    /// What the language supports, as <see cref="ClientLanguages.Register"/> takes it. The variants
    /// (<see cref="ClientLanguageInfo.Variants"/>) come from the built-in table (<see cref="ClientLanguages.GetBuiltIn"/>),
    /// limited to the transports the host reported.
    /// </summary>
    public ClientLanguageInfo ToLanguageInfo(string language)
    {
        var transports = Transports;
        var variants = ClientLanguages.GetBuiltIn(language).Variants
            .Select(v => v with { Transports = v.Transports.Where(transports.Contains).ToArray() })
            .Where(v => v.Transports.Count > 0)
            .ToArray();
        return new(language, transports, Capabilities) { Variants = variants };
    }

    /// <summary>Parses a hello message.</summary>
    /// <exception cref="ControlProtocolException">Not a hello, or a required member is missing.</exception>
    public static HostHello Parse(JsonObject message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message["t"]?.GetValue<string>() != "hello")
            throw new ControlProtocolException("The first control frame is not a hello: " + Truncate(message.ToJsonString()));

        try
        {
            return new HostHello
            {
                Proto = message["proto"]?.GetValue<int>() ?? 0,
                Token = message["token"]?.GetValue<string>() ?? "",
                Lang = message["lang"]?.GetValue<string>() ?? "",
                Runtime = message["runtime"]?.GetValue<string>() ?? "",
                Lib = message["lib"]?.GetValue<string>() ?? "",
                SchemaHash = message["schemaHash"]?.GetValue<string>() ?? "",
                CapabilityNames = Strings(message["capabilities"]),
                TransportNames = Strings(message["transports"]),
                MaxConcurrency = Math.Max(1, message["maxConcurrency"]?.GetValue<int>() ?? 1),
                Pid = message["pid"]?.GetValue<int>(),
            };
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException)
        {
            throw new ControlProtocolException("Malformed hello: " + e.Message, e);
        }
    }

    /// <summary>The JSON of this hello (C# hosts).</summary>
    public JsonObject ToJson()
    {
        var json = new JsonObject
        {
            ["t"] = "hello",
            ["proto"] = Proto,
            ["token"] = Token,
            ["lang"] = Lang,
            ["runtime"] = Runtime,
            ["lib"] = Lib,
            ["schemaHash"] = SchemaHash,
            ["capabilities"] = new JsonArray(CapabilityNames.Select(n => (JsonNode?)n).ToArray()),
            ["transports"] = new JsonArray(TransportNames.Select(n => (JsonNode?)n).ToArray()),
            ["maxConcurrency"] = MaxConcurrency,
        };
        if (Pid != null)
            json["pid"] = Pid;
        return json;
    }

    private static string[] Strings(JsonNode? node)
        => node is JsonArray array ? array.Select(n => n?.GetValue<string>()).OfType<string>().ToArray() : [];

    private static string Truncate(string text) => text.Length <= 200 ? text : text[..200] + "…";
}
