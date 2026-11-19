using System.Collections.Concurrent;

namespace NexNet.Interop;

/// <summary>What a client language supports.</summary>
/// <param name="Name">The language name, as in <c>NEXNET_TEST_LANGS</c>.</param>
/// <param name="Transports">The transports its client implements.</param>
/// <param name="Capabilities">Its optional features.</param>
public sealed record ClientLanguageInfo(string Name, IReadOnlyList<ClientTransport> Transports, ClientCapabilities Capabilities)
{
    /// <summary>Why <paramref name="transport"/> with <paramref name="requires"/> cannot run on this language; null when it can.</summary>
    public string? GetSkipReason(ClientTransport transport, ClientCapabilities requires)
    {
        if (!Transports.Contains(transport))
            return $"The {Name} client has no {transport} transport.";

        var missing = (requires | transport.RequiredCapability()) & ~Capabilities;
        return missing == ClientCapabilities.None
            ? null
            : $"The {Name} client lacks: {missing}.";
    }
}

/// <summary>
/// The client languages of the harness and the <c>NEXNET_TEST_LANGS</c> environment variable that selects them.
/// </summary>
/// <remarks>
/// <c>NEXNET_TEST_LANGS</c> is a comma-separated list of language names. <see cref="CSharp"/> always runs, first;
/// <c>browser</c> means <see cref="BrowserChromium"/> and <c>browsers</c> all three browsers. Unset or empty: only
/// <see cref="CSharp"/> (D27).
/// </remarks>
public static class ClientLanguages
{
    /// <summary>The environment variable listing the languages to test.</summary>
    public const string EnvironmentVariable = "NEXNET_TEST_LANGS";

    /// <summary>The C# client, in process.</summary>
    public const string CSharp = "csharp";

    /// <summary>The JavaScript client on Node.</summary>
    public const string Node = "node";

    /// <summary>The JavaScript client in Chromium.</summary>
    public const string BrowserChromium = "browser-chromium";

    /// <summary>The JavaScript client in Firefox.</summary>
    public const string BrowserFirefox = "browser-firefox";

    /// <summary>The JavaScript client in WebKit.</summary>
    public const string BrowserWebKit = "browser-webkit";

    /// <summary>The Java client.</summary>
    public const string Java = "java";

    private const ClientCapabilities Everything =
        ClientCapabilities.AuthorizationHeader | ClientCapabilities.ClientCertificate | ClientCapabilities.Pinning
        | ClientCapabilities.CustomCa | ClientCapabilities.RemoteCertificate | ClientCapabilities.HttpSocket
        | ClientCapabilities.Tcp | ClientCapabilities.Uds | ClientCapabilities.FakeTime | ClientCapabilities.ClientPool;

    // Browsers: WebSocket only, and none of the TLS or header controls (gotcha 3).
    private const ClientCapabilities Browser = ClientCapabilities.FakeTime | ClientCapabilities.ClientPool;

    private static readonly ClientTransport[] BrowserTransports = [ClientTransport.WebSocket, ClientTransport.WebSocketTls];

    private static readonly ConcurrentDictionary<string, ClientLanguageInfo> Languages = new(StringComparer.Ordinal)
    {
        [CSharp] = new(CSharp, ClientTransports.All, Everything),
        [Node] = new(Node, ClientTransports.All, Everything),
        [BrowserChromium] = new(BrowserChromium, BrowserTransports, Browser),
        [BrowserFirefox] = new(BrowserFirefox, BrowserTransports, Browser),
        [BrowserWebKit] = new(BrowserWebKit, BrowserTransports, Browser),
        [Java] = new(Java, ClientTransports.All.Where(t => t != ClientTransport.Uds).ToArray(), Everything & ~ClientCapabilities.Uds),
    };

    /// <summary>
    /// What <paramref name="language"/> supports. An unknown language gets every transport and no optional capability, so
    /// its rows run and fail visibly when no driver is registered for it.
    /// </summary>
    public static ClientLanguageInfo Get(string language)
        => Languages.TryGetValue(language, out var info)
            ? info
            : new ClientLanguageInfo(language, ClientTransports.All, ClientCapabilities.None);

    /// <summary>Replaces what a language supports (e.g. with what its host reports in its hello).</summary>
    public static void Register(ClientLanguageInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        Languages[info.Name] = info;
    }

    /// <summary>The languages to test: <see cref="CSharp"/>, then those of <c>NEXNET_TEST_LANGS</c>.</summary>
    public static IReadOnlyList<string> FromEnvironment() => Parse(Environment.GetEnvironmentVariable(EnvironmentVariable));

    /// <summary>The languages of a <c>NEXNET_TEST_LANGS</c> value: <see cref="CSharp"/> first, then the listed ones.</summary>
    public static IReadOnlyList<string> Parse(string? value)
    {
        var result = new List<string> { CSharp };
        if (string.IsNullOrWhiteSpace(value))
            return result;

        foreach (var raw in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var name = raw.ToLowerInvariant();
            IEnumerable<string> expanded = name switch
            {
                "browser" => [BrowserChromium],
                "browsers" => [BrowserChromium, BrowserFirefox, BrowserWebKit],
                _ => [name],
            };

            foreach (var language in expanded)
            {
                if (!result.Contains(language))
                    result.Add(language);
            }
        }

        return result;
    }
}
