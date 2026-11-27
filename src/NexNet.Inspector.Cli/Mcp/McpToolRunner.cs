using System.Globalization;
using System.Text;

namespace NexNet.Inspector.Cli;

/// <summary>What a tool call returns: the text for the model, and whether it is a tool error.</summary>
internal readonly record struct McpToolOutcome(string Text, bool IsError)
{
    public static McpToolOutcome Error(string text) => new(text, true);
}

/// <summary>
/// Runs the MCP tools. Each tool is a <c>nexnet-tap</c> command line run in process through <see cref="TapCli"/> with its
/// own writers, so the MCP server prints exactly what the CLI prints (the I45 text renderer, the I47 queries, the I48
/// findings, the I46 tail and filters) and shares its validation and messages. <c>attach</c> and <c>record_start</c>
/// keep their command running in the background (<see cref="McpRun"/>) until <c>detach</c> / <c>record_stop</c>.
/// </summary>
/// <remarks>
/// Tokens are never put on the command line: a call's <c>token</c> argument (or the server's default, from
/// <c>NEXNET_TAP_TOKEN</c> or <c>mcp --token</c>) is handed to <see cref="TapCli.RunAsync"/> as the environment token,
/// and no message repeats it. <c>--include-secrets</c> is never passed: redaction is the server's policy.
/// </remarks>
internal sealed class McpToolRunner(string? defaultToken) : IAsyncDisposable
{
    /// <summary>Most streams and recordings open at once.</summary>
    public const int MaxRuns = 16;

    /// <summary>Most characters one tool result carries; longer output is cut at a line with a note.</summary>
    public const int MaxTextChars = 100_000;

    /// <summary>The token budget a query gets when the call gives neither <c>max_lines</c> nor <c>max_tokens</c>.</summary>
    public const int DefaultQueryTokens = 8000;

    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(30);

    private readonly object _gate = new();
    private readonly Dictionary<string, McpRun> _runs = new(StringComparer.Ordinal);
    private int _streams;
    private int _recordings;

    /// <summary>How long <c>record_stop</c> waits for the footer (<see cref="TapCli.DrainTimeout"/>).</summary>
    public TimeSpan DrainTimeout { get; init; } = TimeSpan.FromSeconds(15);

    public Task<McpToolOutcome> CallAsync(McpToolSpec tool, McpArgs args, CancellationToken cancellationToken) => tool.Kind switch
    {
        McpToolKind.ListSessions => ListAsync(args, cancellationToken),
        McpToolKind.Attach => AttachAsync(args, cancellationToken),
        McpToolKind.Read => ReadAsync(args, cancellationToken),
        McpToolKind.Detach => DetachAsync(args),
        McpToolKind.Findings => FindingsAsync(args, cancellationToken),
        McpToolKind.Diff => DiffAsync(args, cancellationToken),
        McpToolKind.RecordStart => RecordStartAsync(args, cancellationToken),
        McpToolKind.RecordStop => RecordStopAsync(args),
        _ => QueryAsync(tool.Kind, args, cancellationToken),
    };

    // ----- One-shot commands -----

    private Task<McpToolOutcome> ListAsync(McpArgs args, CancellationToken cancellationToken)
    {
        var argv = new List<string> { "list" };
        if (!TryConnect(argv, args, out var token, out var error))
            return Task.FromResult(McpToolOutcome.Error(error));
        return RunAsync(argv, token, cancellationToken);
    }

    private Task<McpToolOutcome> FindingsAsync(McpArgs args, CancellationToken cancellationToken)
    {
        if (!TryFile(args, "file", out var path, out var error))
            return Task.FromResult(McpToolOutcome.Error(error));
        var argv = new List<string> { "findings", path };
        Option(argv, "--session", args.String("session"));
        Option(argv, "--slow", args.String("slow"));
        if (args.String("format") == "ndjson")
            argv.Add("--json");
        return RunAsync(argv, null, cancellationToken);
    }

    private Task<McpToolOutcome> DiffAsync(McpArgs args, CancellationToken cancellationToken)
    {
        if (!TryFile(args, "a", out var a, out var error) || !TryFile(args, "b", out var b, out error))
            return Task.FromResult(McpToolOutcome.Error(error));
        var argv = new List<string> { "diff", a, b };
        Option(argv, "--session", args.String("session"));
        Option(argv, "--top", args.String("top"));
        AddOutput(argv, args);
        return RunAsync(argv, null, cancellationToken);
    }

    // summary, show, trace, around, schema, export_section: a file or a live session.
    private Task<McpToolOutcome> QueryAsync(McpToolKind kind, McpArgs args, CancellationToken cancellationToken)
    {
        var argv = new List<string>
        {
            kind switch
            {
                McpToolKind.Summary => "summary",
                McpToolKind.Show => "show",
                McpToolKind.Trace => "trace",
                McpToolKind.Around => "around",
                McpToolKind.Schema => "schema",
                _ => "export",
            },
        };

        string? token = null;
        var hasFile = args.Has("file");
        var hasEndpoint = args.Has("endpoint");
        string? error;
        if (hasFile == hasEndpoint)
            return Task.FromResult(McpToolOutcome.Error(hasFile
                ? "Give file or endpoint, not both."
                : "Give file (a .nntl recording) or endpoint (a live session; with session, token)."));
        if (hasFile)
        {
            if (!TryFile(args, "file", out var path, out error))
                return Task.FromResult(McpToolOutcome.Error(error));
            argv.Add(path);
            if (args.Has("duration") || args.Has("token") || args.Has("insecure") || args.Has("server_name"))
                return Task.FromResult(McpToolOutcome.Error("duration, token, insecure and server_name apply to a live session (endpoint), not to a file."));
        }
        else
        {
            if (!TryConnect(argv, args, out token, out error))
                return Task.FromResult(McpToolOutcome.Error(error));
            Option(argv, "--duration", args.String("duration"));
        }

        switch (kind)
        {
            case McpToolKind.Show:
            case McpToolKind.Trace:
                if (!TryPositional(argv, args, "id", out error))
                    return Task.FromResult(McpToolOutcome.Error(error));
                if (args.Bool("bytes") == true)
                    argv.Add("--bytes");
                break;
            case McpToolKind.Around:
                if (!TryPositional(argv, args, "t", out error) || !TryPositional(argv, args, "window", out error))
                    return Task.FromResult(McpToolOutcome.Error(error));
                Option(argv, "--level", args.String("level"));
                break;
            case McpToolKind.ExportSection:
                if (!TryPath(args, "out", out var output, out error))
                    return Task.FromResult(McpToolOutcome.Error(error));
                Option(argv, "--out", output);
                Option(argv, "--from", args.String("from"));
                Option(argv, "--to", args.String("to"));
                if (args.Bool("force") == true)
                    argv.Add("--force");
                break;
        }

        Option(argv, "--session", args.String("session"));
        if (kind != McpToolKind.ExportSection)
            AddOutput(argv, args);
        return RunAsync(argv, token, cancellationToken);
    }

    private static async Task<McpToolOutcome> RunAsync(List<string> argv, string? token, CancellationToken cancellationToken)
    {
        var output = new StringWriter(CultureInfo.InvariantCulture) { NewLine = "\n" };
        var error = new StringWriter(CultureInfo.InvariantCulture) { NewLine = "\n" };
        var cli = new TapCli(output, error);
        int code;
        try
        {
            code = await cli.RunAsync([.. argv], token, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return McpToolOutcome.Error("Internal error: " + e.Message);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return Outcome(code, output.ToString(), error.ToString());
    }

    // ----- Streams: attach, read, detach -----

    private async Task<McpToolOutcome> AttachAsync(McpArgs args, CancellationToken cancellationToken)
    {
        var argv = new List<string> { "tail" };
        if (!TryConnect(argv, args, out var token, out var error))
            return McpToolOutcome.Error(error);
        Option(argv, "--session", args.String("session"));
        Option(argv, "--level", args.String("level"));
        Option(argv, "--format", args.String("format"));
        if (args.Bool("collapse") is { } collapse)
            Option(argv, "--collapse", collapse ? "on" : "off");
        Option(argv, "--value-width", args.String("value_width"));
        Option(argv, "--depth", args.String("depth"));
        Option(argv, "--method", args.List("method"));
        Option(argv, "--pipe", args.List("pipe"));
        Option(argv, "--dir", args.String("dir"));
        Option(argv, "--only", args.List("only"));
        Option(argv, "--slow", args.String("slow"));
        Option(argv, "--grep", args.String("grep"));
        if (args.Bool("findings") == true)
            argv.Add("--findings");
        Option(argv, "--until", args.String("until"));
        Option(argv, "--context", args.String("context"));
        Option(argv, "--duration", args.String("duration"));
        Option(argv, "--wait", args.String("wait"));
        return await StartAsync(McpRunKind.Stream, argv, token, cancellationToken).ConfigureAwait(false);
    }

    private async Task<McpToolOutcome> ReadAsync(McpArgs args, CancellationToken cancellationToken)
    {
        if (!TryFind(args.String("stream_id"), McpRunKind.Stream, remove: false, out var run, out var error))
            return McpToolOutcome.Error(error);
        long after = args.Int("after") ?? 0;
        var max = args.Int("max_lines") ?? McpTools.DefaultReadLines;
        var waitMs = args.Int("wait_ms") ?? 0;
        var lines = new List<string>();
        var changed = run.Output.Changed;
        var next = run.Output.Read(after, max, lines, out var dropped);
        if (lines.Count == 0 && waitMs > 0 && !run.Completion.IsCompleted)
        {
            await Task.WhenAny(changed, run.Completion, Task.Delay(waitMs, cancellationToken)).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            next = run.Output.Read(after, max, lines, out dropped);
        }

        run.Advance(next);
        var text = new StringBuilder();
        if (dropped > 0)
            text.Append(CultureInfo.InvariantCulture, $"… {dropped} earlier lines were dropped (a stream keeps its last {run.Output.Capacity}).\n");
        foreach (var line in lines)
            text.Append(line).Append('\n');
        var total = run.Output.Count;
        text.Append(CultureInfo.InvariantCulture, $"… next: after={next} (");
        if (next < total)
            text.Append(CultureInfo.InvariantCulture, $"{total - next} more lines ready; ");
        text.Append(run.Completion.IsCompleted ? "stream ended: " + Ended(run) + "; detach to release it)" : "stream live)");
        return new McpToolOutcome(Cap(text.ToString()), false);
    }

    private async Task<McpToolOutcome> DetachAsync(McpArgs args)
    {
        if (!TryFind(args.String("stream_id"), McpRunKind.Stream, remove: true, out var run, out var error))
            return McpToolOutcome.Error(error);
        await run.StopAsync(StopTimeout).ConfigureAwait(false);
        var lines = new List<string>();
        var next = run.Output.Read(run.ReadTo, McpTools.MaxReadLines, lines, out _);
        var total = run.Output.Count;
        var text = new StringBuilder();
        foreach (var line in lines)
            text.Append(line).Append('\n');
        text.Append(CultureInfo.InvariantCulture, $"… detached {run.Id}: {total} lines in all, {total - run.ReadTo} not read before detach");
        if (next < total)
            text.Append(CultureInfo.InvariantCulture, $" ({total - next} beyond the {McpTools.MaxReadLines} shown)");
        text.Append("; ").Append(Ended(run)).Append('.');
        return new McpToolOutcome(Cap(text.ToString()), false);
    }

    // ----- Recordings -----

    private Task<McpToolOutcome> RecordStartAsync(McpArgs args, CancellationToken cancellationToken)
    {
        var argv = new List<string> { "record" };
        if (!TryConnect(argv, args, out var token, out var error) || !TryPath(args, "out", out var path, out error))
            return Task.FromResult(McpToolOutcome.Error(error));
        Option(argv, "--session", args.String("session"));
        Option(argv, "--out", path);
        if (args.Bool("force") == true)
            argv.Add("--force");
        Option(argv, "--duration", args.String("duration"));
        Option(argv, "--overflow", args.String("overflow"));
        if (args.Bool("snapshot") == false)
            argv.Add("--no-snapshot");
        Option(argv, "--wait", args.String("wait"));
        return StartAsync(McpRunKind.Recording, argv, token, cancellationToken);
    }

    private async Task<McpToolOutcome> RecordStopAsync(McpArgs args)
    {
        if (!TryFind(args.String("record_id"), McpRunKind.Recording, remove: true, out var run, out var error))
            return McpToolOutcome.Error(error);
        await run.StopAsync(DrainTimeout + StopTimeout).ConfigureAwait(false);
        var code = run.Completion.IsCompletedSuccessfully ? run.Completion.Result : (int)ExitCode.Failed;
        var outcome = Outcome(code, run.Output.ToString(), run.Error.ToString());
        return outcome with { Text = $"{run.Id} stopped.\n" + outcome.Text };
    }

    // ----- Background runs -----

    private async Task<McpToolOutcome> StartAsync(McpRunKind kind, List<string> argv, string? token, CancellationToken cancellationToken)
    {
        string id;
        lock (_gate)
        {
            if (_runs.Count >= MaxRuns)
                return McpToolOutcome.Error($"{MaxRuns} streams and recordings are open; detach or record_stop one first ({string.Join(", ", _runs.Keys)}).");
            id = kind == McpRunKind.Stream ? "s" + ++_streams : "r" + ++_recordings;
        }

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = new McpRun(id, kind);
        var cli = new TapCli(run.Output, run.Error)
        {
            TailStarted = () => started.TrySetResult(),
            RecordingStarted = () => started.TrySetResult(),
            DrainTimeout = DrainTimeout,
        };
        run.Completion = Task.Run(() => cli.RunAsync([.. argv], token, run.Stop.Token), CancellationToken.None);

        Task first;
        try
        {
            first = await Task.WhenAny(started.Task, run.Completion).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await run.StopAsync(StopTimeout).ConfigureAwait(false);
            throw;
        }

        if (first == run.Completion)
        {
            var code = run.Completion.IsCompletedSuccessfully ? run.Completion.Result : (int)ExitCode.Failed;
            // Both commands report "started" before they stream, so ending first means they failed to start.
            return Outcome(code == (int)ExitCode.Ok ? (int)ExitCode.Failed : code, run.Output.ToString(), run.Error.ToString());
        }

        lock (_gate)
            _runs.Add(id, run);
        var status = Clean(kind == McpRunKind.Stream ? run.Error.ToString() : run.Output.ToString() + "\n" + run.Error.ToString());
        run.MarkStarted();
        return new McpToolOutcome(kind == McpRunKind.Stream
            ? $"stream_id {id}\n{status}\nread(stream_id: \"{id}\", after: 0) returns its lines; detach(stream_id: \"{id}\") ends it."
            : $"record_id {id}\n{status}\nrecord_stop(record_id: \"{id}\") ends it and reports what was written.", false);
    }

    private bool TryFind(string? id, McpRunKind kind, bool remove, out McpRun run, out string error)
    {
        lock (_gate)
        {
            if (id is not null && _runs.TryGetValue(id, out var found) && found.Kind == kind)
            {
                if (remove)
                    _runs.Remove(id);
                run = found;
                error = "";
                return true;
            }

            var open = new List<string>();
            foreach (var r in _runs.Values)
            {
                if (r.Kind == kind)
                    open.Add(r.Id);
            }

            run = null!;
            var what = kind == McpRunKind.Stream ? "stream" : "recording";
            var from = kind == McpRunKind.Stream ? "attach returns a stream_id" : "record_start returns a record_id";
            error = $"No open {what} '{id}'; {from}. Open: {(open.Count == 0 ? "none" : string.Join(", ", open))}.";
            return false;
        }
    }

    private static string Ended(McpRun run)
    {
        if (!run.Completion.IsCompleted)
            return "still stopping";
        var code = run.Completion.IsCompletedSuccessfully ? run.Completion.Result : (int)ExitCode.Failed;
        var errors = Clean(run.ErrorSinceStart());
        return code == (int)ExitCode.Ok
            ? "ended normally" + (errors.Length > 0 ? " (" + errors.Replace('\n', ' ') + ")" : "")
            : Meaning(code) + (errors.Length > 0 ? ": " + errors.Replace('\n', ' ') : "");
    }

    public async ValueTask DisposeAsync()
    {
        McpRun[] runs;
        lock (_gate)
        {
            runs = [.. _runs.Values];
            _runs.Clear();
        }

        // Recordings get their footer: stopping a run is what Ctrl+C does to the command.
        var stops = new Task[runs.Length];
        for (var i = 0; i < runs.Length; i++)
            stops[i] = runs[i].StopAsync(DrainTimeout + StopTimeout);
        await Task.WhenAll(stops).ConfigureAwait(false);
    }

    // ----- Arguments to command lines -----

    private bool TryConnect(List<string> argv, McpArgs args, out string? token, out string error)
    {
        token = args.String("token") ?? defaultToken;
        error = "";
        if (args.String("endpoint") is not { } endpoint)
        {
            error = "Missing required argument 'endpoint' (tcp://host:port, tls://host:port or uds:/path).";
            return false;
        }

        if (string.IsNullOrEmpty(token))
        {
            error = $"No endpoint token: pass the token argument, or set {CliArguments.TokenVariable} in the MCP server's environment (the \"env\" of its .mcp.json entry).";
            return false;
        }

        Option(argv, "--endpoint", endpoint);
        if (args.Bool("insecure") == true)
            argv.Add("--insecure");
        Option(argv, "--server-name", args.String("server_name"));
        return true;
    }

    private static void AddOutput(List<string> argv, McpArgs args)
    {
        Option(argv, "--format", args.String("format"));
        Option(argv, "--max-lines", args.String("max_lines"));
        Option(argv, "--max-tokens", args.String("max_tokens") ?? (args.Has("max_lines") ? null : DefaultQueryTokens.ToString(CultureInfo.InvariantCulture)));
        Option(argv, "--after", args.String("after"));
        Option(argv, "--value-width", args.String("value_width"));
        Option(argv, "--depth", args.String("depth"));
    }

    // Values always go inline (--name=value), so a value that starts with '-' is never read as an option.
    private static void Option(List<string> argv, string name, string? value)
    {
        if (value is not null)
            argv.Add(name + "=" + value);
    }

    private static bool TryPositional(List<string> argv, McpArgs args, string name, out string error)
    {
        error = "";
        if (args.String(name) is not { } value)
            return true;
        if (value.Length == 0 || value[0] == '-')
        {
            error = $"Argument '{name}' must not be empty or start with '-'.";
            return false;
        }

        argv.Add(value);
        return true;
    }

    private static bool TryFile(McpArgs args, string name, out string path, out string error)
    {
        if (!TryPath(args, name, out path, out error))
            return false;
        if (File.Exists(path))
            return true;
        error = $"Argument '{name}': no file at {path}.";
        return false;
    }

    // Only the paths a call names are read or written; relative ones are resolved against the server's directory.
    private static bool TryPath(McpArgs args, string name, out string path, out string error)
    {
        path = "";
        error = "";
        var value = args.String(name);
        if (string.IsNullOrWhiteSpace(value))
        {
            error = $"Argument '{name}' needs a file path.";
            return false;
        }

        try
        {
            path = Path.GetFullPath(value);
            return true;
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = $"Argument '{name}' is not a usable path: {e.Message}";
            return false;
        }
    }

    // ----- Results -----

    private static McpToolOutcome Outcome(int code, string output, string error)
    {
        var text = output.TrimEnd();
        var notes = Clean(error);
        if (code == (int)ExitCode.Ok)
            return new McpToolOutcome(Cap(text.Length > 0 ? text : notes.Length > 0 ? notes : "ok"), false);
        var message = new StringBuilder(notes);
        if (text.Length > 0)
            message.Append(message.Length > 0 ? "\n" : "").Append(text);
        message.Append(message.Length > 0 ? "\n" : "").Append('(').Append(Meaning(code)).Append(')');
        return McpToolOutcome.Error(Cap(message.ToString()));
    }

    private static string Meaning(int code) => (ExitCode)code switch
    {
        ExitCode.Failed => "failed",
        ExitCode.Usage => "invalid arguments",
        ExitCode.Refused => "refused by the server configuration",
        ExitCode.NotFound => "not found",
        ExitCode.Cancelled => "interrupted",
        _ => string.Create(CultureInfo.InvariantCulture, $"exit {code}"),
    };

    /// <summary>The CLI's stderr as a message: without the program prefix, usage pointer and Ctrl+C hints.</summary>
    internal static string Clean(string error)
    {
        var text = new StringBuilder();
        foreach (var raw in error.Split('\n'))
        {
            var line = raw.TrimEnd('\r', ' ');
            if (line.Length == 0 || line.StartsWith("Run 'nexnet-tap help'", StringComparison.Ordinal))
                continue;
            if (line.StartsWith("nexnet-tap: ", StringComparison.Ordinal))
                line = line["nexnet-tap: ".Length..];
            line = line.Replace(" Press Ctrl+C to stop.", "", StringComparison.Ordinal);
            text.Append(text.Length > 0 ? "\n" : "").Append(line);
        }

        return text.ToString();
    }

    private static string Cap(string text)
    {
        if (text.Length <= MaxTextChars)
            return text;
        var cut = text.LastIndexOf('\n', MaxTextChars);
        if (cut <= 0)
            cut = MaxTextChars;
        return text[..cut] + string.Create(CultureInfo.InvariantCulture,
            $"\n… output cut at {MaxTextChars} characters: pass max_lines or max_tokens and page with after.");
    }
}

/// <summary>What a background run is.</summary>
internal enum McpRunKind : byte
{
    /// <summary>A live tail (<c>attach</c>).</summary>
    Stream,

    /// <summary>A recording (<c>record_start</c>).</summary>
    Recording,
}

/// <summary>A command running in the background: a live tail or a recording, with its output kept for paging.</summary>
internal sealed class McpRun(string id, McpRunKind kind)
{
    private long _readTo;
    private long _errorStart;

    public string Id { get; } = id;

    public McpRunKind Kind { get; } = kind;

    /// <summary>What the command printed to stdout (the tail's lines).</summary>
    public McpLineBuffer Output { get; } = new();

    /// <summary>What it printed to stderr.</summary>
    public McpLineBuffer Error { get; } = new(1000);

    /// <summary>Ctrl+C for the command.</summary>
    public CancellationTokenSource Stop { get; } = new();

    public Task<int> Completion { get; set; } = Task.FromResult(0);

    /// <summary>The highest line cursor a <c>read</c> returned.</summary>
    public long ReadTo => Interlocked.Read(ref _readTo);

    public void Advance(long next)
    {
        long seen;
        while ((seen = Interlocked.Read(ref _readTo)) < next && Interlocked.CompareExchange(ref _readTo, next, seen) != seen)
        {
        }
    }

    /// <summary>Stderr lines written once the run started are what <see cref="ErrorSinceStart"/> reports.</summary>
    public void MarkStarted() => _errorStart = Error.Count;

    public string ErrorSinceStart()
    {
        var lines = new List<string>();
        Error.Read(_errorStart, 1000, lines, out _);
        return string.Join('\n', lines);
    }

    /// <summary>Stops the command as Ctrl+C does and waits for it to end.</summary>
    public async Task StopAsync(TimeSpan timeout)
    {
        try
        {
            await Stop.CancelAsync().ConfigureAwait(false);
            await Completion.WaitAsync(timeout).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Timed out or failed: what it printed is reported as it is.
        }

        Output.Complete();
        Error.Complete();
    }
}
