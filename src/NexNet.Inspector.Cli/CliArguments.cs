using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using NexNet.Inspector.Core.Text;
using NexNet.Inspector.Tap;

namespace NexNet.Inspector.Cli;

/// <summary>A parsed <c>nexnet-tap</c> command line.</summary>
internal sealed partial class CliArguments
{
    /// <summary>Environment variable read when <c>--token</c> is absent, so the token stays out of shell history.</summary>
    public const string TokenVariable = "NEXNET_TAP_TOKEN";

    public static readonly TimeSpan DefaultConnectTimeout = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan DefaultWait = TimeSpan.FromSeconds(30);

    public CliCommand Command { get; private set; }

    /// <summary>The command <c>help</c> asks about; <see cref="CliCommand.None"/> for the overview.</summary>
    public CliCommand HelpTopic { get; private set; }

    /// <summary>Options given on the command line.</summary>
    public CliOption Given { get; private set; }

    public EndpointAddress Endpoint { get; private set; }

    /// <summary>The endpoint token from <c>--token</c> or <see cref="TokenVariable"/>.</summary>
    public string? Token { get; private set; }

    public string? ServerName { get; private set; }

    public bool Insecure => Has(CliOption.Insecure);

    public TimeSpan ConnectTimeout { get; private set; } = DefaultConnectTimeout;

    public SessionSelectorKind Selector { get; private set; }

    /// <summary>The session ID when <see cref="Selector"/> is <see cref="SessionSelectorKind.Id"/>.</summary>
    public long SessionId { get; private set; }

    /// <summary>How long to record; null records until Ctrl+C or the session closes.</summary>
    public TimeSpan? Duration { get; private set; }

    /// <summary>How long <c>--session first</c> waits for a session to appear.</summary>
    public TimeSpan Wait { get; private set; } = DefaultWait;

    public string? Out { get; private set; }

    public InspectorOverflow Overflow { get; private set; } = InspectorOverflow.Drop;

    public bool IncludeSecrets => Has(CliOption.IncludeSecrets);

    public bool Snapshot => !Has(CliOption.NoSnapshot);

    public bool Force => Has(CliOption.Force);

    public bool Json => Has(CliOption.Json);

    public bool Messages => Has(CliOption.Messages);

    /// <summary>`show --bytes`: byte ranges and hex.</summary>
    public bool Bytes => Has(CliOption.Bytes);

    /// <summary>`slow --over`: the threshold; null for the default.</summary>
    public TimeSpan? Over { get; private set; }

    /// <summary>`collections --contents`: list the items.</summary>
    public bool Contents => Has(CliOption.Contents);

    /// <summary>`export --from`: a time since the session's start, as typed.</summary>
    public string? From { get; private set; }

    /// <summary>`export --to`: a time since the session's start, as typed.</summary>
    public string? To { get; private set; }

    /// <summary>`diff --top`: entities whose field diffs are listed.</summary>
    public int Top { get; private set; } = 5;

    /// <summary>The recording file of <c>info</c> and <c>repair</c>: the first bare argument.</summary>
    public string? File => Positionals.Count > 0 ? Positionals[0] : null;

    /// <summary>Bare arguments after the verb, up to the command's <see cref="CliCommandSpec.Positionals"/>.</summary>
    public List<string> Positionals { get; } = [];

    public bool Has(CliOption option) => (Given & option) != 0;

    /// <summary>Parses a command line.</summary>
    /// <param name="args">The arguments after the program name.</param>
    /// <param name="environmentToken">The value of <see cref="TokenVariable"/>, or null.</param>
    /// <param name="result">The parsed arguments.</param>
    /// <param name="error">Why the command line is invalid.</param>
    public static bool TryParse(ReadOnlySpan<string> args, string? environmentToken,
        [NotNullWhen(true)] out CliArguments? result, [NotNullWhen(false)] out string? error)
    {
        result = null;
        var parsed = new CliArguments();
        if (args.Length == 0)
        {
            parsed.Command = CliCommand.Help;
            result = parsed;
            error = null;
            return true;
        }

        if (!TryParseCommand(args[0], out var command))
        {
            if (args[0].StartsWith('-') && ParseOptionName(args[0].AsSpan()) == CliOption.Help)
                command = CliCommand.Help;
            else if (args[0] is "--version")
                command = CliCommand.Version;
            else
            {
                error = $"Unknown command '{args[0]}'.";
                return false;
            }
        }

        parsed.Command = command;
        if (command is CliCommand.Help or CliCommand.Version)
        {
            if (command == CliCommand.Help && args.Length > 1 && TryParseCommand(args[1], out var topic))
                parsed.HelpTopic = topic;
            result = parsed;
            error = null;
            return true;
        }

        for (var i = 1; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg.Length < 2 || arg[0] != '-')
            {
                if (parsed.Positionals.Count < (CliCommands.Get(command)?.Positionals ?? 0))
                {
                    parsed.Positionals.Add(arg);
                    continue;
                }

                error = $"Unexpected argument '{arg}'.";
                return false;
            }

            var name = arg.AsSpan();
            string? inlineValue = null;
            var equals = name.IndexOf('=');
            if (equals > 0)
            {
                inlineValue = arg[(equals + 1)..];
                name = name[..equals];
            }

            var option = ParseOptionName(name);
            if (option == CliOption.None)
            {
                error = $"Unknown option '{name}'.";
                return false;
            }

            if (option == CliOption.Help)
            {
                parsed.HelpTopic = command;
                parsed.Command = CliCommand.Help;
                result = parsed;
                error = null;
                return true;
            }

            if ((Allowed(command) & option) == 0)
            {
                error = $"Option '{name}' does not apply to '{Name(command)}'.";
                return false;
            }

            if (parsed.Has(option))
            {
                error = $"Option '{name}' is given more than once.";
                return false;
            }

            parsed.Given |= option;
            if (!TakesValue(option))
            {
                if (inlineValue is not null)
                {
                    error = $"Option '{name}' takes no value.";
                    return false;
                }

                continue;
            }

            var value = inlineValue;
            if (value is null)
            {
                if (i + 1 >= args.Length)
                {
                    error = $"Option '{name}' needs a value.";
                    return false;
                }

                value = args[++i];
            }

            if (!parsed.TrySetValue(option, value, out error))
                return false;
        }

        if (!parsed.TryValidate(environmentToken, out error))
            return false;

        result = parsed;
        return true;
    }

    /// <summary>
    /// Parses a duration: a sequence of numbers with the units <c>ms</c>, <c>s</c>, <c>m</c>, <c>h</c> (<c>30s</c>,
    /// <c>5m</c>, <c>1h30m</c>), or a bare number of seconds. Must be positive.
    /// </summary>
    public static bool TryParseDuration(ReadOnlySpan<char> text, out TimeSpan duration)
    {
        duration = TimeSpan.Zero;
        if (text.IsEmpty)
            return false;

        if (double.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var bareSeconds))
        {
            if (bareSeconds <= 0 || bareSeconds > TimeSpan.MaxValue.TotalSeconds / 2)
                return false;
            duration = TimeSpan.FromSeconds(bareSeconds);
            return true;
        }

        var total = 0.0;
        while (!text.IsEmpty)
        {
            var digits = 0;
            while (digits < text.Length && (char.IsAsciiDigit(text[digits]) || text[digits] == '.'))
                digits++;
            if (digits == 0
                || !double.TryParse(text[..digits], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var amount))
                return false;
            text = text[digits..];

            var unit = 0;
            while (unit < text.Length && char.IsAsciiLetterLower(text[unit]))
                unit++;
            double scale;
            switch (text[..unit])
            {
                case "ms":
                    scale = 0.001;
                    break;
                case "s":
                    scale = 1;
                    break;
                case "m":
                    scale = 60;
                    break;
                case "h":
                    scale = 3600;
                    break;
                default:
                    return false;
            }

            total += amount * scale;
            text = text[unit..];
        }

        if (total <= 0 || total > TimeSpan.MaxValue.TotalSeconds / 2)
            return false;
        duration = TimeSpan.FromSeconds(total);
        return true;
    }

    public static string Name(CliCommand command) => CliCommands.Get(command)?.Name ?? "";

    /// <summary>Options each command accepts (<see cref="CliCommands.All"/>).</summary>
    public static CliOption Allowed(CliCommand command) => CliCommands.Get(command)?.Allowed ?? CliOption.None;

    private static bool TryParseCommand(string text, out CliCommand command) => CliCommands.TryParse(text, out command);

    private static CliOption ParseOptionName(ReadOnlySpan<char> name) => name switch
    {
        "--endpoint" or "-e" => CliOption.Endpoint,
        "--token" or "-t" => CliOption.Token,
        "--insecure" => CliOption.Insecure,
        "--server-name" => CliOption.ServerName,
        "--connect-timeout" => CliOption.ConnectTimeout,
        "--session" or "-s" => CliOption.Session,
        "--duration" or "-d" => CliOption.Duration,
        "--out" or "-o" => CliOption.Out,
        "--overflow" => CliOption.Overflow,
        "--include-secrets" => CliOption.IncludeSecrets,
        "--no-snapshot" => CliOption.NoSnapshot,
        "--wait" => CliOption.Wait,
        "--force" or "-f" => CliOption.Force,
        "--json" => CliOption.Json,
        "--messages" => CliOption.Messages,
        "--help" or "-h" or "-?" => CliOption.Help,
        "--level" => CliOption.Level,
        "--format" => CliOption.Format,
        "--collapse" => CliOption.Collapse,
        "--value-width" => CliOption.ValueWidth,
        "--depth" => CliOption.Depth,
        "--max-lines" => CliOption.MaxLines,
        "--max-tokens" => CliOption.MaxTokens,
        "--after" => CliOption.After,
        "--method" => CliOption.Method,
        "--pipe" => CliOption.Pipe,
        "--dir" => CliOption.Dir,
        "--only" => CliOption.Only,
        "--slow" => CliOption.Slow,
        "--grep" => CliOption.Grep,
        "--sample" => CliOption.Sample,
        "--until" => CliOption.Until,
        "--context" => CliOption.Context,
        "--follow" => CliOption.Follow,
        "--bytes" => CliOption.Bytes,
        "--over" => CliOption.Over,
        "--contents" => CliOption.Contents,
        "--from" => CliOption.From,
        "--to" => CliOption.To,
        "--top" => CliOption.Top,
        "--findings" => CliOption.Findings,
        _ => CliOption.None,
    };

    private static bool TakesValue(CliOption option) => option is CliOption.Endpoint or CliOption.Token
        or CliOption.ServerName or CliOption.ConnectTimeout or CliOption.Session or CliOption.Duration or CliOption.Out
        or CliOption.Overflow or CliOption.Wait
        or CliOption.Over or CliOption.From or CliOption.To or CliOption.Top
        || (option & TailValueOptions) != 0;

    private bool TrySetValue(CliOption option, string value, [NotNullWhen(false)] out string? error)
    {
        error = null;
        switch (option)
        {
            case CliOption.Endpoint:
                if (!EndpointAddress.TryParse(value, out var endpoint, out error))
                    return false;
                Endpoint = endpoint;
                return true;
            case CliOption.Token:
                if (value.Length == 0)
                {
                    error = "--token is empty.";
                    return false;
                }

                Token = value;
                return true;
            case CliOption.ServerName:
                ServerName = value;
                return true;
            case CliOption.Out:
                Out = value;
                return true;
            case CliOption.Session:
                return TrySetSession(value, out error);
            case CliOption.Level:
            case CliOption.Format:
            case CliOption.Collapse:
            case CliOption.ValueWidth:
            case CliOption.Depth:
            case CliOption.MaxLines:
            case CliOption.MaxTokens:
            case CliOption.After:
            case CliOption.Method:
            case CliOption.Pipe:
            case CliOption.Dir:
            case CliOption.Only:
            case CliOption.Slow:
            case CliOption.Grep:
            case CliOption.Sample:
            case CliOption.Until:
            case CliOption.Context:
                return TrySetTailValue(option, value, out error);
            case CliOption.Overflow:
                switch (value)
                {
                    case "drop":
                        Overflow = InspectorOverflow.Drop;
                        return true;
                    case "block":
                        Overflow = InspectorOverflow.Block;
                        return true;
                    default:
                        error = $"--overflow must be 'drop' or 'block', not '{value}'.";
                        return false;
                }
            case CliOption.ConnectTimeout:
            case CliOption.Duration:
            case CliOption.Wait:
                if (!TryParseDuration(value, out var duration))
                {
                    error = $"'{value}' is not a duration (examples: 30s, 5m, 1h, 1h30m, 250ms).";
                    return false;
                }

                if (option == CliOption.Duration)
                    Duration = duration;
                else if (option == CliOption.Wait)
                    Wait = duration;
                else
                    ConnectTimeout = duration;
                return true;
            case CliOption.Over:
            case CliOption.From:
            case CliOption.To:
            case CliOption.Top:
                return TrySetQueryValue(option, value, out error);
            default:
                error = $"Option {option} takes no value.";
                return false;
        }
    }

    // The query commands' options (QueryCommand).
    private bool TrySetQueryValue(CliOption option, string value, [NotNullWhen(false)] out string? error)
    {
        error = null;
        switch (option)
        {
            case CliOption.Over:
                if (!TryParseDuration(value, out var over))
                {
                    error = $"'{value}' is not a duration (examples: 30s, 250ms).";
                    return false;
                }

                Over = over;
                return true;
            case CliOption.From:
                From = value;
                return true;
            case CliOption.To:
                To = value;
                return true;
        }

        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var top) || top <= 0)
        {
            error = $"--top must be a positive whole number, not '{value}'.";
            return false;
        }

        Top = top;
        return true;
    }

    private bool TrySetSession(string value, [NotNullWhen(false)] out string? error)
    {
        error = null;
        switch (value)
        {
            case "first":
                Selector = SessionSelectorKind.First;
                return true;
            case "all":
                Selector = SessionSelectorKind.All;
                return true;
        }

        // The text format's session ID: @0x1BCB (hex).
        var hex = value.AsSpan();
        if (hex.StartsWith("@"))
            hex = hex[1..];
        if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            && long.TryParse(hex[2..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var hexId) && hexId != 0)
        {
            Selector = SessionSelectorKind.Id;
            SessionId = hexId;
            return true;
        }

        if (!long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var id) || id == 0)
        {
            error = $"--session must be a session ID, 'first' or 'all', not '{value}'.";
            return false;
        }

        Selector = SessionSelectorKind.Id;
        SessionId = id;
        return true;
    }

    private bool TryValidate(string? environmentToken, [NotNullWhen(false)] out string? error)
    {
        error = null;
        var name = Name(Command);
        switch (Command)
        {
            case CliCommand.List:
            case CliCommand.Record:
                if (!Has(CliOption.Endpoint))
                {
                    error = $"'{name}' needs --endpoint (tcp://host:port, tls://host:port or uds:/path).";
                    return false;
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

                if (Command == CliCommand.Record)
                {
                    if (!Has(CliOption.Session))
                    {
                        error = "'record' needs --session <id|first|all>.";
                        return false;
                    }

                    if (!Has(CliOption.Out))
                    {
                        error = "'record' needs --out <file.nntl>.";
                        return false;
                    }

                    if (Has(CliOption.Wait) && Selector != SessionSelectorKind.First)
                    {
                        error = "--wait applies to --session first only.";
                        return false;
                    }
                }

                return true;
            case CliCommand.Tail:
                return TryValidateTail(environmentToken, out error);
            case CliCommand.Summary:
            case CliCommand.Show:
            case CliCommand.Trace:
            case CliCommand.Around:
            case CliCommand.Errors:
            case CliCommand.Slow:
            case CliCommand.Pipes:
            case CliCommand.Collections:
            case CliCommand.Schema:
            case CliCommand.Diff:
            case CliCommand.Export:
                if (Has(CliOption.Endpoint))
                {
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
                }

                return QueryCommand.TryValidate(this, out error);
            case CliCommand.Info:
            case CliCommand.Repair:
            case CliCommand.Findings:
                if (File is null)
                {
                    error = $"'{name}' needs a recording file.";
                    return false;
                }

                return true;
            default:
                return true;
        }
    }
}
