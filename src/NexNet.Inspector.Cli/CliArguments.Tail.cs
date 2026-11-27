using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using NexNet.Inspector.Core.Text;

namespace NexNet.Inspector.Cli;

/// <summary>The options of <c>nexnet-tap tail</c> (see <see cref="TailCommand"/>).</summary>
internal sealed partial class CliArguments
{
    /// <summary>Default for <c>--slow</c>.</summary>
    public static readonly TimeSpan DefaultSlow = TimeSpan.FromMilliseconds(100);

    /// <summary>Default for <c>--context</c>.</summary>
    public const int DefaultContext = 10;

    /// <summary>Tail options that take a value.</summary>
    public const CliOption TailValueOptions = CliOption.Level | CliOption.Format | CliOption.Collapse | CliOption.ValueWidth
                                              | CliOption.Depth | CliOption.MaxLines | CliOption.MaxTokens | CliOption.After
                                              | CliOption.Method | CliOption.Pipe | CliOption.Dir | CliOption.Only | CliOption.Slow
                                              | CliOption.Grep | CliOption.Sample | CliOption.Until | CliOption.Context;

    /// <summary>Every option <c>tail</c> accepts.</summary>
    public const CliOption TailOptions = CliCommands.ConnectOptions | CliOption.Session | CliOption.Duration | CliOption.Wait
                                         | CliOption.Follow | TailValueOptions;

    public TextLevel Level { get; private set; } = TextLevel.Messages;

    public TextFormat Format { get; private set; } = TextFormat.Text;

    public bool Collapse { get; private set; } = true;

    public int ValueWidth { get; private set; } = TextOptions.Default.ValueWidth;

    public int Depth { get; private set; } = TextOptions.Default.Depth;

    /// <summary>Most lines a tail writes; 0 for no limit.</summary>
    public int MaxLines { get; private set; }

    /// <summary>Most approximate tokens a tail writes; 0 for no limit.</summary>
    public long MaxTokens { get; private set; }

    /// <summary>The <c>--after</c> cursor.</summary>
    public TextCursor? After { get; private set; }

    /// <summary><c>--method</c> globs.</summary>
    public string[] Methods { get; private set; } = [];

    /// <summary><c>--pipe</c> IDs.</summary>
    public TailPipeId[] Pipes { get; private set; } = [];

    public TextDirections Directions { get; private set; } = TextDirections.Both;

    public TailOnly Only { get; private set; }

    /// <summary>The <c>--slow</c> threshold.</summary>
    public TimeSpan Slow { get; private set; } = DefaultSlow;

    public string? Grep { get; private set; }

    /// <summary>Keep one line in this many (<c>--sample 1/N</c>); 1 keeps all.</summary>
    public int Sample { get; private set; } = 1;

    public TailUntil? Until { get; private set; }

    /// <summary>Lines printed before the line that met <c>--until</c>.</summary>
    public int Context { get; private set; } = DefaultContext;

    public bool Follow => Has(CliOption.Follow);

    /// <summary>True when <c>tail</c> reads a live endpoint rather than a file.</summary>
    public bool IsLiveTail => Has(CliOption.Endpoint);

    /// <summary>The recording <c>tail</c> replays; null for a live tail.</summary>
    public string? TailFile { get; private set; }

    private bool TrySetTailValue(CliOption option, string value, [NotNullWhen(false)] out string? error)
    {
        error = null;
        switch (option)
        {
            case CliOption.Level:
                switch (value)
                {
                    case "summary":
                        Level = TextLevel.Summary;
                        return true;
                    case "messages":
                        Level = TextLevel.Messages;
                        return true;
                    case "frames":
                        Level = TextLevel.Frames;
                        return true;
                    case "bytes":
                        Level = TextLevel.Bytes;
                        return true;
                    default:
                        error = $"--level must be summary, messages, frames or bytes, not '{value}'.";
                        return false;
                }
            case CliOption.Format:
                switch (value)
                {
                    case "text":
                        Format = TextFormat.Text;
                        return true;
                    case "ndjson":
                        Format = TextFormat.Ndjson;
                        return true;
                    default:
                        error = $"--format must be text or ndjson, not '{value}'.";
                        return false;
                }
            case CliOption.Collapse:
                switch (value)
                {
                    case "on":
                        Collapse = true;
                        return true;
                    case "off":
                        Collapse = false;
                        return true;
                    default:
                        error = $"--collapse must be on or off, not '{value}'.";
                        return false;
                }
            case CliOption.ValueWidth:
                if (!TryParseCount(value, 4, out var width))
                {
                    error = $"--value-width must be a whole number of at least 4, not '{value}'.";
                    return false;
                }

                ValueWidth = width;
                return true;
            case CliOption.Depth:
                if (!TryParseCount(value, 0, out var depth))
                {
                    error = $"--depth must be a whole number of at least 0, not '{value}'.";
                    return false;
                }

                Depth = depth;
                return true;
            case CliOption.MaxLines:
                if (!TryParseCount(value, 1, out var lines))
                {
                    error = $"--max-lines must be a positive whole number, not '{value}'.";
                    return false;
                }

                MaxLines = lines;
                return true;
            case CliOption.MaxTokens:
                if (!TryParseCount(value, 1, out var tokens))
                {
                    error = $"--max-tokens must be a positive whole number, not '{value}'.";
                    return false;
                }

                MaxTokens = tokens;
                return true;
            case CliOption.Context:
                if (!TryParseCount(value, 0, out var context))
                {
                    error = $"--context must be a whole number of at least 0, not '{value}'.";
                    return false;
                }

                Context = context;
                return true;
            case CliOption.After:
                if (!TextCursor.TryParse(value, out var cursor))
                {
                    error = $"--after: '{value}' is not a cursor (copy it from a '--after <cursor>' line).";
                    return false;
                }

                After = cursor;
                return true;
            case CliOption.Method:
                if (!TrySplit(value, out var methods))
                {
                    error = "--method needs at least one method name or glob (Send*, *Message).";
                    return false;
                }

                Methods = methods;
                return true;
            case CliOption.Pipe:
                if (!TrySplit(value, out var pipeTexts))
                {
                    error = "--pipe needs at least one pipe ID (4.4, ≋4·4, or 4).";
                    return false;
                }

                var pipes = new TailPipeId[pipeTexts.Length];
                for (var i = 0; i < pipes.Length; i++)
                {
                    if (!TailPipeId.TryParse(pipeTexts[i], out pipes[i]))
                    {
                        error = $"--pipe: '{pipeTexts[i]}' is not a pipe ID (4.4, ≋4·4, or 4).";
                        return false;
                    }
                }

                Pipes = pipes;
                return true;
            case CliOption.Dir:
                switch (value)
                {
                    case "in":
                        Directions = TextDirections.Inbound;
                        return true;
                    case "out":
                        Directions = TextDirections.Outbound;
                        return true;
                    default:
                        error = $"--dir must be in or out, not '{value}'.";
                        return false;
                }
            case CliOption.Only:
                if (!TrySplit(value, out var kinds))
                {
                    error = "--only needs errors, slow, cancel or gaps.";
                    return false;
                }

                foreach (var kind in kinds)
                {
                    var flag = kind switch
                    {
                        "errors" or "error" => TailOnly.Errors,
                        "slow" => TailOnly.Slow,
                        "cancel" or "cancels" => TailOnly.Cancel,
                        "gaps" or "gap" => TailOnly.Gaps,
                        _ => TailOnly.None,
                    };
                    if (flag == TailOnly.None)
                    {
                        error = $"--only takes errors, slow, cancel or gaps (comma separated), not '{kind}'.";
                        return false;
                    }

                    Only |= flag;
                }

                return true;
            case CliOption.Slow:
                if (!TryParseDuration(value, out var slow))
                {
                    error = $"'{value}' is not a duration (examples: 30s, 5m, 1h, 1h30m, 250ms).";
                    return false;
                }

                Slow = slow;
                return true;
            case CliOption.Grep:
                if (value.Length == 0)
                {
                    error = "--grep is empty.";
                    return false;
                }

                Grep = value;
                return true;
            case CliOption.Sample:
                if (!TryParseSample(value, out var sample))
                {
                    error = $"--sample must be 1/N (or N) with N at least 1, not '{value}'.";
                    return false;
                }

                Sample = sample;
                return true;
            case CliOption.Until:
                if (!TailUntil.TryParse(value, out var until, out var problem))
                {
                    error = "--until: " + problem;
                    return false;
                }

                Until = until;
                return true;
            default:
                error = $"Option {option} takes no value.";
                return false;
        }
    }

    private bool TryValidateTail(string? environmentToken, [NotNullWhen(false)] out string? error)
    {
        error = null;
        if (Positionals.Count > 0)
        {
            var target = Positionals[0];
            if (target.StartsWith("tcp://", StringComparison.OrdinalIgnoreCase) || target.StartsWith("tls://", StringComparison.OrdinalIgnoreCase)
                || target.StartsWith("uds:", StringComparison.OrdinalIgnoreCase))
            {
                if (Has(CliOption.Endpoint))
                {
                    error = "'tail' takes the endpoint once: as the argument or as --endpoint.";
                    return false;
                }

                if (!EndpointAddress.TryParse(target, out var endpoint, out error))
                    return false;
                Endpoint = endpoint;
                Given |= CliOption.Endpoint;
            }
            else if (Has(CliOption.Endpoint))
            {
                error = "'tail' reads an endpoint or a recording file, not both.";
                return false;
            }
            else
            {
                TailFile = target;
            }
        }

        if (!Has(CliOption.Endpoint) && TailFile is null)
        {
            error = "'tail' needs an endpoint (tcp://host:port, tls://host:port or uds:/path) or a recording file.";
            return false;
        }

        if (Has(CliOption.Context) && Until is null)
        {
            error = "--context applies to --until only.";
            return false;
        }

        if (After is { } after && after.Level != Level)
        {
            error = $"--after: the cursor was made at level {after.Level.ToString().ToLowerInvariant()}; pass --level {after.Level.ToString().ToLowerInvariant()}.";
            return false;
        }

        if (Has(CliOption.Slow))
            Only |= TailOnly.Slow;

        if (Has(CliOption.Wait) && (!Has(CliOption.Session) || Selector != SessionSelectorKind.First))
        {
            error = "--wait applies to --session first only.";
            return false;
        }

        if (TailFile is not null)
        {
            if (Has(CliOption.Token | CliOption.Insecure | CliOption.ServerName | CliOption.ConnectTimeout | CliOption.Wait))
            {
                error = "Connection options and --wait apply to a live tail (an endpoint), not to a recording file.";
                return false;
            }

            return true;
        }

        if (Endpoint.Scheme != EndpointScheme.Tls && Has(CliOption.Insecure | CliOption.ServerName))
        {
            error = "--insecure and --server-name apply to tls:// endpoints only.";
            return false;
        }

        Token ??= string.IsNullOrEmpty(environmentToken) ? null : environmentToken;
        if (Token is null)
        {
            error = $"No endpoint token: pass --token or set {TokenVariable}.";
            return false;
        }

        if (!Has(CliOption.Session))
        {
            error = "A live 'tail' needs --session <id|first|all>.";
            return false;
        }

        if (After is not null)
        {
            error = "--after resumes a recording file; a live attach starts at the attach (record it, then tail the file to page).";
            return false;
        }

        return true;
    }

    private static bool TryParseCount(string text, int minimum, out int value) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value >= minimum;

    private static bool TryParseSample(string text, out int sample)
    {
        var span = text.AsSpan().Trim();
        if (span.StartsWith("1/"))
            span = span[2..];
        return int.TryParse(span, NumberStyles.None, CultureInfo.InvariantCulture, out sample) && sample >= 1;
    }

    private static bool TrySplit(string text, out string[] parts)
    {
        parts = text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length > 0;
    }
}
