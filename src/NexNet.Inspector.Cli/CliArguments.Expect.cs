using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using NexNet.Inspector.Core.Findings;

namespace NexNet.Inspector.Cli;

/// <summary>A <c>--p99</c> expectation: calls of methods matching <see cref="Method"/> have a p99 under <see cref="Ms"/>.</summary>
/// <param name="Method">A method name or glob (<c>*</c>, <c>?</c>, case-insensitive).</param>
/// <param name="Ms">The bound in milliseconds.</param>
/// <param name="Inclusive">True for <c>&lt;=</c>: the p99 may equal the bound.</param>
/// <param name="Text">As typed, for the report.</param>
internal readonly record struct ExpectLatency(string Method, double Ms, bool Inclusive, string Text);

/// <summary>What a <c>--max-findings</c> limit counts.</summary>
internal enum ExpectFindingScope : byte
{
    /// <summary>Every finding.</summary>
    All = 0,

    /// <summary>Findings of a severity or worse.</summary>
    Severity = 1,

    /// <summary>Findings of one kind.</summary>
    Kind = 2,
}

/// <summary>A <c>--max-findings</c> limit: at most <see cref="Max"/> findings of a severity (or worse), of a kind, or in all.</summary>
internal readonly record struct ExpectFindingLimit(ExpectFindingScope Scope, FindingSeverity Severity, FindingKind Kind, int Max, string Text);

/// <summary>The options of <c>nexnet-tap expect</c> (see <see cref="ExpectCommand"/>).</summary>
internal sealed partial class CliArguments
{
    /// <summary>Every expectation option; <c>expect</c> needs at least one.</summary>
    public const CliOption ExpectOptions = CliOption.NoGaps | CliOption.NoErrors | CliOption.P99 | CliOption.MaxFindings;

    /// <summary><c>--p99</c> bounds.</summary>
    public ExpectLatency[] Latencies { get; private set; } = [];

    /// <summary><c>--max-findings</c> limits.</summary>
    public ExpectFindingLimit[] FindingLimits { get; private set; } = [];

    private bool TrySetExpectValue(CliOption option, string value, [NotNullWhen(false)] out string? error)
    {
        error = null;
        if (!TrySplit(value, out var parts))
        {
            error = option == CliOption.P99
                ? "--p99 needs a method and a bound: 'Send*<50ms' (quote it in a shell)."
                : "--max-findings needs limits: error=0, warn=3, slow=0 or all=5.";
            return false;
        }

        if (option == CliOption.P99)
        {
            var latencies = new ExpectLatency[parts.Length];
            for (var i = 0; i < parts.Length; i++)
            {
                if (!TryParseLatency(parts[i], out latencies[i]))
                {
                    error = $"--p99: '{parts[i]}' is not a method and a bound (Send*<50ms, Join<=1s, *<100ms).";
                    return false;
                }
            }

            Latencies = latencies;
            return true;
        }

        var limits = new ExpectFindingLimit[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!TryParseFindingLimit(parts[i], out limits[i]))
            {
                error = $"--max-findings: '{parts[i]}' is not a limit: error|high, warn|medium, info|low, a kind (slow, gap, …) "
                        + "or all, then =N (error=0).";
                return false;
            }
        }

        FindingLimits = limits;
        return true;
    }

    private static bool TryParseLatency(string text, out ExpectLatency latency)
    {
        latency = default;
        var at = text.IndexOf('<');
        if (at <= 0)
            return false;
        var inclusive = at + 1 < text.Length && text[at + 1] == '=';
        var method = text[..at].Trim();
        var bound = text[(at + (inclusive ? 2 : 1))..].Trim();
        if (method.Length == 0 || !QueryCommand.TryParseTime(bound, out var ms) || ms <= 0)
            return false;
        latency = new ExpectLatency(method, ms, inclusive, text);
        return true;
    }

    private static bool TryParseFindingLimit(string text, out ExpectFindingLimit limit)
    {
        limit = default;
        var at = text.IndexOf('=');
        var name = at < 0 ? "all" : text[..at].Trim();
        var count = at < 0 ? text : text[(at + 1)..];
        if (!int.TryParse(count.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var max))
            return false;
        switch (name)
        {
            case "all":
            case "total":
                limit = new ExpectFindingLimit(ExpectFindingScope.All, FindingSeverity.Info, default, max, text);
                return true;
            case "error":
            case "errors":
            case "high":
                limit = new ExpectFindingLimit(ExpectFindingScope.Severity, FindingSeverity.Error, default, max, text);
                return true;
            case "warn":
            case "warning":
            case "warnings":
            case "medium":
                limit = new ExpectFindingLimit(ExpectFindingScope.Severity, FindingSeverity.Warning, default, max, text);
                return true;
            case "info":
            case "low":
                limit = new ExpectFindingLimit(ExpectFindingScope.Severity, FindingSeverity.Info, default, max, text);
                return true;
        }

        for (var kind = FindingKind.ProtocolError; kind <= FindingKind.ReconnectLoop; kind++)
        {
            if (FindingsText.Code(kind) == name)
            {
                limit = new ExpectFindingLimit(ExpectFindingScope.Kind, FindingSeverity.Info, kind, max, text);
                return true;
            }
        }

        return false;
    }

    private bool TryValidateExpect(string? environmentToken, [NotNullWhen(false)] out string? error)
    {
        error = null;
        if (Positionals.Count > 0 && IsEndpointText(Positionals[0]))
        {
            if (Has(CliOption.Endpoint))
            {
                error = "'expect' takes the endpoint once: as the argument or as --endpoint.";
                return false;
            }

            if (!EndpointAddress.TryParse(Positionals[0], out var endpoint, out error))
                return false;
            Endpoint = endpoint;
            Given |= CliOption.Endpoint;
            Positionals.Clear();
        }
        else if (Positionals.Count > 0 && Has(CliOption.Endpoint))
        {
            error = "'expect' reads an endpoint or a recording file, not both.";
            return false;
        }

        if (!Has(CliOption.Endpoint) && Positionals.Count == 0)
        {
            error = "'expect' needs a recording file or an endpoint (tcp://host:port, tls://host:port or uds:/path).";
            return false;
        }

        if ((Given & ExpectOptions) == 0)
        {
            error = "'expect' needs at least one expectation: --no-gaps, --no-errors, --p99 or --max-findings.";
            return false;
        }

        if (!Has(CliOption.Endpoint))
        {
            if (Has(CliCommands.ConnectOptions | CliOption.Duration | CliOption.NoSnapshot))
            {
                error = "Connection options, --duration and --no-snapshot apply to a live endpoint, not to a recording file.";
                return false;
            }

            return true;
        }

        if (Endpoint.Scheme != EndpointScheme.Tls && Has(CliOption.Insecure | CliOption.ServerName))
        {
            error = "--insecure and --server-name apply to tls:// endpoints only.";
            return false;
        }

        if (Has(CliOption.Session) && Selector == SessionSelectorKind.All)
        {
            error = "A live 'expect' checks one session: --session <id|first>.";
            return false;
        }

        Token ??= string.IsNullOrEmpty(environmentToken) ? null : environmentToken;
        if (Token is null)
        {
            error = $"No endpoint token: pass --token or set {TokenVariable}.";
            return false;
        }

        return true;
    }

    private static bool IsEndpointText(string text) =>
        text.StartsWith("tcp://", StringComparison.OrdinalIgnoreCase) || text.StartsWith("tls://", StringComparison.OrdinalIgnoreCase)
        || text.StartsWith("uds:", StringComparison.OrdinalIgnoreCase);
}
