using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using NexNet.Inspector.Core.Compare;
using NexNet.Inspector.Core.Format;
using NexNet.Inspector.Core.Model;
using NexNet.Inspector.Core.Session;
using NexNet.Inspector.Core.Text;
using NexNet.Inspector.Tap;

namespace NexNet.Inspector.Cli;

/// <summary>
/// The drill-down queries (`summary`, `show`, `trace`, `around`, `errors`, `slow`, `pipes`, `collections`, `schema`, `diff`,
/// `export`, `format`): read a recording, or attach to a live session for `--duration` and query what arrived, then
/// print Core's <see cref="TextQuery"/> lines through <see cref="TextQueryWriter"/> (text or ndjson, budgets, cursors).
/// </summary>
internal static class QueryCommand
{
    /// <summary>Output options every query takes.</summary>
    public const CliOption Output = CliOption.Format | CliOption.MaxLines | CliOption.MaxTokens | CliOption.After | CliOption.ValueWidth
                                    | CliOption.Depth;

    /// <summary>Options of a query that reads a file or a live session.</summary>
    public const CliOption Live = Output | CliCommands.ConnectOptions | CliOption.Session | CliOption.Duration | CliOption.NoSnapshot;

    /// <summary>How long a live query attaches when <c>--duration</c> is not given.</summary>
    public static readonly TimeSpan DefaultDuration = TimeSpan.FromSeconds(2);

    private const double DefaultAroundMs = 25;
    private static readonly TimeSpan DefaultOver = TimeSpan.FromMilliseconds(100);
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    /// <summary>Checks the bare arguments a query command needs.</summary>
    public static bool TryValidate(CliArguments args, [NotNullWhen(false)] out string? error)
    {
        error = null;
        var live = args.Has(CliOption.Endpoint);
        var name = CliArguments.Name(args.Command);
        var given = args.Positionals.Count - (live ? 0 : 1);
        if (!live && args.Positionals.Count == 0)
        {
            error = args.Command == CliCommand.Diff
                ? "'diff' needs two recording files."
                : $"'{name}' needs a recording file, or --endpoint and --session for a live session.";
            return false;
        }

        switch (args.Command)
        {
            case CliCommand.Diff:
                if (live || args.Positionals.Count != 2)
                {
                    error = "'diff' needs two recording files.";
                    return false;
                }

                return true;
            case CliCommand.Show:
            case CliCommand.Trace:
                if (given != 1)
                {
                    error = $"'{name}' needs an ID: #5 (or 5), 5.2, f18, p4 or ≋4·4.";
                    return false;
                }

                return true;
            case CliCommand.Around:
                if (given is < 1 or > 2)
                {
                    error = "'around' needs a time in ms since the session's start (+27.5), and optionally a window (±10).";
                    return false;
                }

                return true;
            case CliCommand.Export:
                if (!args.Has(CliOption.Out))
                {
                    error = "'export' needs --out <file.nntl>.";
                    return false;
                }

                break;
        }

        if (given != 0)
        {
            error = $"Unexpected argument '{args.Positionals[^1]}'.";
            return false;
        }

        return true;
    }

    /// <summary>Runs a query command.</summary>
    public static async Task<ExitCode> RunAsync(TapCli cli, CliArguments args, CancellationToken cancellationToken)
    {
        var options = OptionsFor(args);
        if (args.Command == CliCommand.Diff)
            return Diff(cli, args, options);

        var live = args.Has(CliOption.Endpoint);
        var rest = live ? args.Positionals : args.Positionals.GetRange(1, args.Positionals.Count - 1);
        double atMs = 0, windowMs = DefaultAroundMs;
        if (args.Command == CliCommand.Around
            && (!TryParseTime(rest[0], out atMs) || (rest.Count > 1 && !TryParseTime(rest[1], out windowMs))))
        {
            await cli.Error.WriteLineAsync($"nexnet-tap: '{string.Join(' ', rest)}' is not a time and window (examples: +27.5, 1.2s, ±10ms).")
                .ConfigureAwait(false);
            return ExitCode.Usage;
        }

        RecordingFile? recording = null;
        List<CaptureSession> sessions;
        if (live)
        {
            var (session, code) = await AttachAsync(cli, args, cancellationToken).ConfigureAwait(false);
            if (session is null)
                return code;
            sessions = [session];
        }
        else
        {
            var opened = Open(cli, args.Positionals[0], args, out recording, out sessions);
            if (opened != ExitCode.Ok)
                return opened;
        }

        try
        {
            if (args.Command == CliCommand.Export)
                return Export(cli, args, sessions, options);

            var query = args.Command == CliCommand.Around
                ? string.Create(Invariant, $"around {atMs} {windowMs} {options.Level}")
                : CliArguments.Name(args.Command) + " " + string.Join(' ', rest) + (args.Bytes ? " --bytes" : "") + (args.Contents ? " --contents" : "")
                  + (args.Over is { } o ? string.Create(Invariant, $" --over {o.TotalMilliseconds}") : "") + " " + options.Level;
            var results = new List<(long SessionId, List<TextRecord> Records)>();
            var found = 0;
            foreach (var session in sessions)
            {
                var records = new List<TextRecord>();
                var q = new TextQuery(session, options);
                switch (args.Command)
                {
                    case CliCommand.Summary:
                        q.Summary(records);
                        break;
                    case CliCommand.Show:
                    case CliCommand.Trace:
                        var count = args.Command == CliCommand.Show ? q.Show(rest[0], args.Bytes, records) : q.Trace(rest[0], records);
                        if (count < 0)
                        {
                            await cli.Error.WriteLineAsync($"nexnet-tap: '{rest[0]}' is not an ID (#5 or 5, 5.2, f18, p4, ≋4·4).").ConfigureAwait(false);
                            return ExitCode.Usage;
                        }

                        found += count;
                        if (count == 0)
                            continue;
                        break;
                    case CliCommand.Around:
                        q.Around(atMs, windowMs, records);
                        break;
                    case CliCommand.Errors:
                        q.Errors(records);
                        break;
                    case CliCommand.Slow:
                        q.Slow((args.Over ?? DefaultOver).TotalMilliseconds, records);
                        break;
                    case CliCommand.Pipes:
                        q.Pipes(records);
                        break;
                    case CliCommand.Collections:
                        q.Collections(args.Contents, records);
                        break;
                    case CliCommand.Schema:
                        q.Schema(records);
                        break;
                }

                results.Add((session.SessionId, records));
            }

            if (args.Command is CliCommand.Show or CliCommand.Trace && found == 0)
            {
                await cli.Error.WriteLineAsync($"nexnet-tap: no session holds {rest[0]}.").ConfigureAwait(false);
                return ExitCode.NotFound;
            }

            return Write(cli, options, query, results);
        }
        finally
        {
            recording?.Dispose();
        }
    }

    /// <summary><c>help format</c>: the legend of the text format.</summary>
    public static ExitCode WriteLegend(TapCli cli)
    {
        cli.Output.Write(TextLegend.Text);
        return ExitCode.Ok;
    }

    private static TextOptions OptionsFor(CliArguments args)
    {
        // show and trace read one entity: wider values and deeper trees than a stream of lines.
        var detail = args.Command is CliCommand.Show or CliCommand.Trace;
        return new TextOptions
        {
            Level = args.Level,
            Format = args.Format,
            ValueWidth = args.Has(CliOption.ValueWidth) ? args.ValueWidth : detail ? 64 : TextOptions.Default.ValueWidth,
            Depth = args.Has(CliOption.Depth) ? args.Depth : detail ? 8 : TextOptions.Default.Depth,
            MaxLines = args.MaxLines,
            MaxTokens = args.MaxTokens,
            After = args.After,
        };
    }

    private static ExitCode Write(TapCli cli, TextOptions options, string query, List<(long SessionId, List<TextRecord> Records)> results)
    {
        TextQueryWriter writer;
        try
        {
            writer = new TextQueryWriter(options, query);
        }
        catch (ArgumentException)
        {
            cli.Error.WriteLine("nexnet-tap: --after: the cursor was made by another query.");
            return ExitCode.Usage;
        }

        writer.Write(cli.Output, results);
        return ExitCode.Ok;
    }

    // Opens a recording and indexes the sessions --session picks (all by default).
    internal static ExitCode Open(TapCli cli, string path, CliArguments args, out RecordingFile? recording, out List<CaptureSession> sessions)
    {
        sessions = [];
        recording = null;
        try
        {
            recording = RecordingFile.Open(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NntlFormatException)
        {
            cli.Error.WriteLine($"nexnet-tap: cannot read {path}: {e.Message}");
            return ExitCode.Failed;
        }

        foreach (var open in recording.Sessions)
        {
            var pick = args.Has(CliOption.Session)
                ? args.Selector switch
                {
                    SessionSelectorKind.Id => open.SessionId == args.SessionId,
                    SessionSelectorKind.First => sessions.Count == 0,
                    _ => true,
                }
                : true;
            if (!pick)
                continue;
            var session = recording.CreateSession(open.SessionId);
            recording.Index(session);
            sessions.Add(session);
        }

        if (sessions.Count == 0)
        {
            cli.Error.WriteLine(args.Has(CliOption.Session) && args.Selector == SessionSelectorKind.Id
                ? string.Create(Invariant, $"nexnet-tap: {path} has no session {args.SessionId} (@0x{args.SessionId:X}).")
                : $"nexnet-tap: {path} holds no session.");
            recording.Dispose();
            recording = null;
            return ExitCode.NotFound;
        }

        return ExitCode.Ok;
    }

    // Attaches to a live session for --duration and returns what arrived.
    internal static async Task<(CaptureSession? Session, ExitCode Code)> AttachAsync(TapCli cli, CliArguments args, CancellationToken cancellationToken)
    {
        var client = await EndpointSession.ConnectAsync(cli, args, cancellationToken).ConfigureAwait(false);
        if (client is null)
            return (null, ExitCode.Failed);

        await using (client.ConfigureAwait(false))
        {
            var capture = new LiveCapture();
            InspectorStream stream;
            try
            {
                var info = await client.GetServerInfoAsync().ConfigureAwait(false);
                // An export writes the session to a file: the recording policy applies, as for record.
                var record = args.Command == CliCommand.Export;
                if (record && !info.AllowRecording)
                {
                    await cli.Error.WriteLineAsync("Recording disabled by server config").ConfigureAwait(false);
                    return (null, ExitCode.Refused);
                }

                var sessions = await client.ListSessionsAsync().ConfigureAwait(false);
                InspectorSessionInfo? found = null;
                foreach (var s in sessions)
                {
                    if (!args.Has(CliOption.Session) || args.Selector != SessionSelectorKind.Id || s.Id == args.SessionId || s.ConnectedId == args.SessionId)
                    {
                        found = s;
                        break;
                    }
                }

                if (found is not { } target)
                {
                    await cli.Error.WriteLineAsync(args.Selector == SessionSelectorKind.Id && args.Has(CliOption.Session)
                        ? $"nexnet-tap: no live session {args.SessionId} on {args.Endpoint}; see 'nexnet-tap list'."
                        : $"nexnet-tap: {args.Endpoint} has no live session.").ConfigureAwait(false);
                    return (null, ExitCode.NotFound);
                }

                var kind = info.Role == NntlSessionRole.Client ? InspectorTargetKind.Client : InspectorTargetKind.Session;
                stream = await client.AttachAsync(new InspectorAttachRequest(kind, target.Id, InspectorOverflow.Drop, args.Snapshot, Record: record),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (InspectorRequestException e)
            {
                await cli.Error.WriteLineAsync("nexnet-tap: the endpoint refused: " + EndpointSession.Describe(e.Status)).ConfigureAwait(false);
                return (null, EndpointSession.ToExitCode(e.Status));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return (null, ExitCode.Cancelled);
            }

            await using (stream.ConfigureAwait(false))
            {
                using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                stop.CancelAfter(args.Duration ?? DefaultDuration);
                try
                {
                    while (await stream.ReadAsync(stop.Token).ConfigureAwait(false))
                    {
                        capture.Accept(stream.Current);
                        if (capture.IsEnded)
                            break;
                    }
                }
                catch (OperationCanceledException)
                {
                    // --duration elapsed (or Ctrl+C): query what arrived.
                }
                catch (Exception e) when (e is IOException or InvalidOperationException or NntlFormatException)
                {
                    await cli.Error.WriteLineAsync($"nexnet-tap: the stream ended early: {e.Message}").ConfigureAwait(false);
                }

                try
                {
                    await client.DetachAsync().WaitAsync(cli.DrainTimeout, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // The endpoint is gone or the stream already ended; what arrived is queried.
                }
            }

            if (capture.Session is not { } session)
            {
                await cli.Error.WriteLineAsync("nexnet-tap: the session sent nothing within --duration.").ConfigureAwait(false);
                return (null, ExitCode.Failed);
            }

            return (session, ExitCode.Ok);
        }
    }

    private static ExitCode Diff(TapCli cli, CliArguments args, TextOptions options)
    {
        RecordingFile? a = null, b = null;
        try
        {
            var code = OpenOne(cli, args.Positionals[0], args, out a, out var sessionA);
            if (code != ExitCode.Ok)
                return code;
            code = OpenOne(cli, args.Positionals[1], args, out b, out var sessionB);
            if (code != ExitCode.Ok)
                return code;

            var compare = new CompareSession(sessionA!, sessionB!);
            var records = new List<TextRecord>();
            TextQuery.Diff(compare, options, args.Top, records);
            return Write(cli, options, string.Create(Invariant, $"diff --top {args.Top}"), [(sessionA!.SessionId, records)]);
        }
        finally
        {
            a?.Dispose();
            b?.Dispose();
        }
    }

    // One session of a recording: the --session one, or the first.
    private static ExitCode OpenOne(TapCli cli, string path, CliArguments args, out RecordingFile? recording, out CaptureSession? session)
    {
        session = null;
        var code = Open(cli, path, args, out recording, out var sessions);
        if (code != ExitCode.Ok)
            return code;
        session = sessions[0];
        return ExitCode.Ok;
    }

    private static ExitCode Export(TapCli cli, CliArguments args, List<CaptureSession> sessions, TextOptions options)
    {
        if (sessions.Count != 1)
        {
            cli.Error.WriteLine($"nexnet-tap: the recording holds {sessions.Count} sessions; pick one with --session.");
            return ExitCode.Usage;
        }

        var session = sessions[0];
        double fromMs = 0, toMs = double.MaxValue;
        if ((args.From is { } from && !TryParseTime(from, out fromMs)) || (args.To is { } to && !TryParseTime(to, out toMs)))
        {
            cli.Error.WriteLine("nexnet-tap: --from and --to are times in ms since the session's start (examples: +27.5, 1.2s).");
            return ExitCode.Usage;
        }

        long fromTicks, toTicks;
        lock (session.Gate)
        {
            var frequency = session.Header.StopwatchFrequency;
            fromTicks = session.StartTicks + TimeWindow.TicksFromMilliseconds(fromMs, frequency);
            toTicks = args.To is null ? Math.Max(session.EndTicks, fromTicks) : session.StartTicks + TimeWindow.TicksFromMilliseconds(toMs, frequency);
        }

        var path = Path.GetFullPath(args.Out!);
        if (File.Exists(path) && !args.Force)
        {
            cli.Error.WriteLine($"nexnet-tap: {path} exists; pass --force to overwrite it.");
            return ExitCode.Failed;
        }

        CaptureRangeResult result;
        try
        {
            using var file = new FileStream(path, args.Force ? FileMode.Create : FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
            result = CaptureExport.WriteRange(session, file, new CaptureExportRange(fromTicks, toTicks));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            cli.Error.WriteLine($"nexnet-tap: cannot write {path}: {e.Message}");
            return ExitCode.Failed;
        }

        var q = new TextQuery(session, options);
        var records = new List<TextRecord> { q.Header() };
        var span = args.To is null ? "end" : string.Create(Invariant, $"+{toMs:0.0}");
        var line = string.Create(Invariant, $"exported +{fromMs:0.0}..{span}: {result.Frames} frames, {result.Ops} ops, {result.Gaps} gaps, ")
                   + TextValuesSize(result.Bytes)
                   + (result.Handshake ? ", handshake" : "") + (result.Snapshot ? ", snapshot" : "") + " -> " + path;
        records.Add(new TextRecord { Kind = TextRecordKind.Stat, Value = line, Count = 1, Bytes = -1, Frame = -1, Entity = -1 });
        return Write(cli, options with { After = null }, "export", [(session.SessionId, records)]);
    }

    private static string TextValuesSize(long bytes) => TextValues.AppendSize(new System.Text.StringBuilder(), bytes).ToString();

    /// <summary>
    /// A time or window in milliseconds as the text format writes it: <c>+27.5</c>, <c>27.5</c>, <c>±10</c>; with a unit
    /// <c>ms</c>, <c>s</c> or <c>m</c> (<c>1.2s</c>). A bare number is milliseconds.
    /// </summary>
    public static bool TryParseTime(string text, out double ms)
    {
        ms = 0;
        var span = text.AsSpan().Trim();
        if (span.StartsWith("+-") || span.StartsWith("-+"))
            span = span[2..];
        else if (span.Length > 0 && span[0] is '+' or '±')
            span = span[1..];
        var digits = 0;
        while (digits < span.Length && (char.IsAsciiDigit(span[digits]) || span[digits] == '.'))
            digits++;
        if (digits == 0 || !double.TryParse(span[..digits], NumberStyles.AllowDecimalPoint, Invariant, out var value))
            return false;
        double scale;
        switch (span[digits..])
        {
            case "":
            case "ms":
                scale = 1;
                break;
            case "s":
                scale = 1000;
                break;
            case "m":
                scale = 60_000;
                break;
            default:
                return false;
        }

        ms = value * scale;
        return true;
    }

    private const string OutputHelp = """
              --format <f>         text (default) or ndjson: one JSON object per line, short keys
              --max-lines <n>      Stop after n lines; the last line is "… budget hit, N withheld, --after <cursor>"
              --max-tokens <n>     Stop before n approximate tokens (chars/4, non-ASCII ×3)
              --after <cursor>     Resume the same query where a budget stopped it
              --value-width <n>    Cut each value at n characters (default 32; show/trace 64)
              --depth <n>          Nesting shown inside values (default 2; show/trace 8)

        """;

    private const string SourceHelp = """
        Source: a recording file (every session, or --session <id|@0x..|first>), or a live session: --endpoint <addr>
        --session <id|first> [--duration <d>] (default 2s) [--no-snapshot] with the connection options of 'record'
        (--token, --insecure, --server-name, --connect-timeout). Live queries read what arrived during --duration.

        """;

    private const string Ids = """
        IDs: #5 or 5 (invocation; quote '#5' in a shell, where # starts a comment), 5.2 (2nd use of wire ID 5),
        ≋4·4 / p4·4 / p4.4 / p4 (pipe), f18 (18th frame). 'nexnet-tap help format' explains the line format.

        """;

    internal static readonly string SummaryUsage = """
        Usage: nexnet-tap summary <file.nntl> [options]

        At most about 30 lines per session: totals, handshake, calls per method with p50/p99/max, errors and
        cancellations, pipes, the largest messages, window stalls, gaps, noise, what was open at the end, and the
        disconnect reason.

        """ + SourceHelp + "Options:\n" + OutputHelp;

    internal static readonly string ShowUsage = """
        Usage: nexnet-tap show <file.nntl> <id> [--bytes] [options]

        One entity as a compact YAML-like tree: an invocation's line, method, state, arguments and result (or
        exception) decoded field by field, its frames and pipe; a pipe's properties and lines; a frame's properties.

        Options:
              --bytes              Add each field's byte range in its message (@offset+size), and a frame's hex

        """ + OutputHelp + Ids + SourceHelp;

    internal static readonly string TraceUsage = """
        Usage: nexnet-tap trace <file.nntl> <id> [options]

        For each message of the entity (an invocation's request and result, a pipe's data runs, a frame's message):
        the frames that carried it and the bytes each carried (msg offset+size), the transport reads or writes they
        arrived in, and the frames interleaved with it on the wire.

        Options:

        """ + OutputHelp + Ids + SourceHelp;

    internal static readonly string AroundUsage = """
        Usage: nexnet-tap around <file.nntl> <t> [±w] [--level messages|frames|bytes] [options]

        What happened within w (default ±25ms) of t, in ms since the session's start as the lines show it (+27.5);
        units ms, s or m (1.2s). Messages level by default.

        Options:
              --level <l>          messages (default), frames or bytes

        """ + OutputHelp + SourceHelp;

    internal static readonly string ErrorsUsage = """
        Usage: nexnet-tap errors <file.nntl> [options]

        Calls that ended in an exception, unauthorized or refused, refused pipes, frames that did not frame or read
        (malformed, incomplete, body malformed), and a disconnect that was not graceful. "errors none" otherwise.

        Options:
              --level <l>          messages (default) or frames

        """ + OutputHelp + SourceHelp;

    internal static readonly string SlowUsage = """
        Usage: nexnet-tap slow <file.nntl> [--over <d>] [options]

        Calls that took at least --over (default 100ms; 250ms, 1s, a bare number is seconds), slowest first.

        Options:
              --over <d>           The threshold

        """ + OutputHelp + SourceHelp;

    internal static readonly string PipesUsage = """
        Usage: nexnet-tap pipes <file.nntl> [options]

        One line per pipe: what it carries, the call that opened it, its state, bytes and runs per direction, the
        window each side granted, items, and how long it was open.

        Options:

        """ + OutputHelp + SourceHelp;

    internal static readonly string CollectionsUsage = """
        Usage: nexnet-tap collections <file.nntl> [--contents] [options]

        One line per synchronized collection: version, messages, and items rebuilt from the messages the server sent.

        Options:
              --contents           List the items as of the end of the capture

        """ + OutputHelp + SourceHelp;

    internal static readonly string SchemaUsage = """
        Usage: nexnet-tap schema <file.nntl> [options]

        The session's schema as compact signatures with wire IDs: server methods (#1 Join(string name) -> UserInfo,
        (oneway), -> ok), collections (☰4 Users), client methods, and the declared types they use.

        Options:

        """ + OutputHelp + SourceHelp;

    internal static readonly string DiffUsage = """
        Usage: nexnet-tap diff <a.nntl> <b.nntl> [--session <id>] [--top <n>] [options]

        Compares a session of each file (the first, or --session's in both) as the app's Compare does: every entity
        whose sides differ and every capture gap, worst first (high: only one side or another outcome; medium:
        content, items or a gap; low: framing; info: timing), times in ms on the shared timeline; then the field
        diffs of the --top (default 5) worst entities both sides hold (~ changed, + only in B, - only in A).

        Options:
              --top <n>            Entities whose field diffs are listed

        """ + OutputHelp;

    internal static readonly string ExportUsage = """
        Usage: nexnet-tap export <file.nntl> --out <section.nntl> [--from <t>] [--to <t>] [--force] [options]

        Writes a section of one session as a recording that opens on its own: the handshake, the schema, a snapshot
        of what was open at --from, and the frames that start between --from and --to (ms since the session's start,
        default the whole session). A live export needs the server to allow recording.

        Options:
          -o, --out <file>         Output file; an existing file is kept unless --force
              --from <t>           Section start (+27.5, 1.2s)
              --to <t>             Section end
          -f, --force              Overwrite --out

        """ + SourceHelp;
}
