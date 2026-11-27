using NexNet.Inspector.Core.Format;
using NexNet.Inspector.Tap;

namespace NexNet.Inspector.Cli;

/// <summary>
/// The <c>nexnet-tap</c> commands. <see cref="Program"/> wires the console in; tests call <see cref="RunAsync"/> with
/// their own writers.
/// </summary>
internal sealed class TapCli(TextWriter output, TextWriter error)
{
    public TextWriter Output { get; } = output;

    public TextWriter Error { get; } = error;

    /// <summary>Called once <c>record</c> has its stream and the output file is open; tests wait on it.</summary>
    public Action? RecordingStarted { get; init; }

    /// <summary>Called once a live &lt;c&gt;tail&lt;/c&gt; has its stream; tests wait on it.</summary>
    public Action? TailStarted { get; init; }

    /// <summary>
    /// How long <c>record</c> waits, after it asked the endpoint to stop, for the close blocks and the index footer.
    /// </summary>
    public TimeSpan DrainTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Parses and runs a command line.</summary>
    /// <param name="args">The arguments after the program name.</param>
    /// <param name="environmentToken">The value of <c>NEXNET_TAP_TOKEN</c>, or null.</param>
    /// <param name="cancellationToken">Ctrl+C: stops <c>record</c> cleanly.</param>
    /// <returns>The process exit code (<see cref="ExitCode"/>).</returns>
    public async Task<int> RunAsync(string[] args, string? environmentToken, CancellationToken cancellationToken)
    {
        if (!CliArguments.TryParse(args, environmentToken, out var parsed, out var problem))
        {
            await Error.WriteLineAsync("nexnet-tap: " + problem).ConfigureAwait(false);
            await Error.WriteLineAsync("Run 'nexnet-tap help' for usage.").ConfigureAwait(false);
            return (int)ExitCode.Usage;
        }

        var spec = CliCommands.Get(parsed.Command) ?? CliCommands.Get(CliCommand.Help)!;
        var code = await spec.Run(this, parsed, cancellationToken).ConfigureAwait(false);
        return (int)code;
    }

    /// <summary>Writes the usage text: the overview, or one command's options.</summary>
    public ExitCode WriteUsage(CliCommand topic)
    {
        Output.Write(CliCommands.Get(topic)?.Usage ?? Overview);
        return ExitCode.Ok;
    }

    public ExitCode WriteVersion()
    {
        Output.WriteLine($"nexnet-tap: inspector API v{NexusInspector.ApiVersion}, .nntl format v{NntlFormat.Version}");
        return ExitCode.Ok;
    }

    private const string ConnectHelp = """
          -e, --endpoint <addr>    Inspector endpoint: tcp://host:port, tls://host:port or uds:/path
          -t, --token <token>      Endpoint token (default: the NEXNET_TAP_TOKEN environment variable)
              --insecure           tls:// only: accept any server certificate (self-signed dev certificates)
              --server-name <name> tls:// only: the name the server certificate must carry (default: the host)
              --connect-timeout <d> Connect and TLS handshake timeout (default 10s)

        """;

    private const string Durations = """
        Durations: a number with a unit, ms, s, m or h (250ms, 30s, 5m, 1h, 1h30m); a bare number is seconds.

        """;

    private const string ExitCodes = """
        Exit codes:
          0    success (record: also when stopped by Ctrl+C or --duration, or when the session closed)
          1    failure: endpoint unreachable or token refused, I/O error, unreadable file
          2    invalid command line
          3    refused by the server configuration ("Recording disabled by server config", secrets not included)
          4    session not found, or none appeared within --wait
          130  record interrupted by Ctrl+C before recording started

        """;

    private static readonly string Overview = """
        nexnet-tap: record and summarise NexNet inspector captures (.nntl).

        Usage:
          nexnet-tap list   --endpoint <addr> [connection options]
          nexnet-tap record --endpoint <addr> --session <id|first|all> --out <file.nntl> [options]
          nexnet-tap info   <file.nntl> [--json] [--messages]
          nexnet-tap repair <file.nntl> [--force]
          nexnet-tap tail   <file.nntl | endpoint> [--session <id|first|all>] [options]
          nexnet-tap summary|errors|pipes|collections|schema <file.nntl> [options]
          nexnet-tap show|trace <file.nntl> <id> [options]
          nexnet-tap around <file.nntl> <t> [±w] [options]
          nexnet-tap slow <file.nntl> [--over <d>] [options]
          nexnet-tap diff <a.nntl> <b.nntl> [options]
          nexnet-tap export <file.nntl> --out <section.nntl> [--from <t>] [--to <t>]
          nexnet-tap findings <file.nntl> [--session <id|first|all>] [--slow <d>] [--json]
          nexnet-tap help [command]
          nexnet-tap help format
          nexnet-tap --version

        Commands:
          list     Show the inspected server's (or client's) live sessions and its recording policy.
          record   Record one session, the first one, or all sessions (new ones from their first byte) into a .nntl
                   file until Ctrl+C, --duration, or the session closes.
          info     Summarise a recording: header, index, damage, and per session times, ops, bytes, frames, gaps,
                   schema and close reason.
          repair   Rebuild the index footer of a crashed or truncated recording, in place.
          tail     Print a recording or a live session as condensed text, one line per message, with filters,
                   budgets and --until.
          findings Rank what the detectors notice in a recording: slow calls, stalls, lost results, errors, gaps.

        Queries (a recording, or a live session with --endpoint; text or ndjson, budgets with --max-lines/--max-tokens):
          summary      About 30 lines: calls per method with p50/p99, errors, cancellations, pipes, largest
                       messages, window stalls, gaps, what was open at the end, the disconnect.
          show         One invocation, pipe or frame as a decoded tree (--bytes adds byte ranges).
          trace        The frames, bytes and transport ops that carried an entity's messages, and what interleaved.
          around       What happened near a time.        errors   Failed calls, bad frames, abnormal disconnects.
          slow         Calls slower than --over.          pipes    One line per pipe.
          collections  Synchronized collections (--contents lists their items).
          schema       Method signatures with wire IDs, collections and types.
          diff         Two sessions compared, worst difference first, with field diffs.
          export       A time section of a session as its own .nntl.
          format       The text format's legend (also 'help format').

        """ + ExitCodes;

    internal static readonly string ListUsage = """
        Usage: nexnet-tap list --endpoint <addr> [options]

        Shows the endpoint's role, recording policy and schema hash, then one line per live session.

        Options:

        """ + ConnectHelp + Durations + ExitCodes;

    internal static readonly string RecordUsage = """
        Usage: nexnet-tap record --endpoint <addr> --session <id|first|all> --out <file.nntl> [options]

        Records until Ctrl+C, --duration, or the recorded session closes, then waits for the endpoint's close blocks
        and index footer. A second Ctrl+C stops at once (run 'nexnet-tap repair' on the file afterwards).
        Recording must be allowed by the server's configuration (InspectorOptions.AllowRecording).

        Options:
          -s, --session <sel>      A session ID from 'list', 'first' (the first listed; waits for one), or 'all'
                                   (current sessions from now, new sessions from their first byte)
          -o, --out <file>         Output file; an existing file is kept unless --force
          -d, --duration <d>       Stop after this long
              --overflow <mode>    drop (default): drop data and record a gap when the recorder falls behind;
                                   block: lossless, but slows the inspected connection
              --no-snapshot        Do not start attached sessions with a state snapshot
              --wait <d>           How long --session first waits for a session (default 30s)
              --include-secrets    Require raw auth tokens; fails unless the server config includes secrets
          -f, --force              Overwrite --out

        """ + ConnectHelp + Durations + ExitCodes;

    internal static readonly string InfoUsage = """
        Usage: nexnet-tap info <file.nntl> [--json] [--messages]

        Summarises a recording: format version, wall-clock anchor, producer, where the index came from (footer or scan)
        and any damage; per session: id, role, transport, endpoints, start/end time and duration, ops and bytes per
        direction, frames per direction, gaps (dropped ops and bytes), schema hash, and close reason.

        Options:
              --json               Write the summary as JSON
              --messages           Also assemble messages (greetings, invocations, pipes); holds the recording's
                                   bytes in memory

        """ + ExitCodes;

    internal static readonly string TailUsage = """
        Usage: nexnet-tap tail <file.nntl> [options]
               nexnet-tap tail <endpoint> --session <id|first|all> [connection options] [options]

        Prints a capture as token-condensed text (docs/internals/inspector-text-format.md): a header per session, then
        one line per message, pings and repeats collapsed, values cut. A file is replayed and the command ends; a live
        endpoint (tcp://, tls://, uds:, or --endpoint) is followed until Ctrl+C, --duration, --until, a budget, or the
        session closes. Lines are flushed one by one. Ctrl+C ends with a stop line and, for a file, its resume cursor.

        Output:
              --level <l>          summary, messages (default), frames or bytes
              --format <f>         text (default) or ndjson
              --collapse on|off    Collapse pings, credits, windows, item runs and repeated lines (default on)
              --value-width <n>    Longest value text before it is cut with … (default 32)
              --depth <n>          Nesting shown inside a value (default 2)
        Filters (header and END lines always pass):
              --method <globs>     Calls of these methods, comma separated (Send*,Join)
              --pipe <ids>         Lines of these pipes: 4.4, ≋4·4, or 4 (either side's ID)
              --dir in|out         One direction, seen from the tapped side
              --only <what>        errors, slow, cancel, gaps (comma separated: any of them)
              --slow <d>           Slow-call threshold (default 100ms); alone, it also keeps slow calls only
              --grep <text>        Lines containing this text (case-insensitive)
        Budgets:
              --max-lines <n>      Stop after n lines with '… budget hit, N withheld, --after <cursor>'
              --max-tokens <n>     The same for approximate tokens (characters / 4)
              --sample 1/N         Keep one line in N (header and END lines always kept)
          -d, --duration <d>       Stop after this long
        Stopping and resuming:
              --until <expr>       Print nothing until a line meets <expr>, then the --context lines before it and that
                                   line, and stop. Terms: method=<glob>, state=ok|exception|canceled|unauthorized|
                                   refused|pending|oneway, error, gap, end, type=<line kind or frame type>; ',' = and,
                                   '|' = or (method=Send*,state=exception|gap). Exit 4 when it never held.
              --context <n>        Lines printed before the --until line (default 10)
              --follow             Keep reading a recording as it grows (a file being recorded), like tail -f
              --findings           Also print the findings detectors' '!' lines as they are detected (see 'help findings')
              --after <cursor>     Resume a file where a budget or stop line said (same --level and filters)
          -s, --session <sel>      A session ID, 'first' or 'all' (live: required; all needs recording allowed. File:
                                   default all, in file order)
              --wait <d>           How long --session first waits for a session (default 30s)

        """ + ConnectHelp + Durations + """
        Exit codes:
          0    success: the source ended, Ctrl+C, --duration, a budget, or --until met
          1    failure: endpoint unreachable or token refused, I/O error, unreadable file
          2    invalid command line, or an --after cursor of another session, level or filters
          3    refused by the server configuration (--session all needs recording allowed)
          4    session not found, or the --until condition never held
          130  interrupted by Ctrl+C before the tail started

        """;

    internal static readonly string RepairUsage = """
        Usage: nexnet-tap repair <file.nntl> [--force]

        Rebuilds the index footer in place: cuts off a torn tail and any old footer, then writes a fresh index. A file
        whose footer is intact is left untouched unless --force.

        Options:
          -f, --force              Rebuild even when the footer is intact

        """ + ExitCodes;

    internal static readonly string FindingsUsage = """
        Usage: nexnet-tap findings <file.nntl> [--session <id|first|all>] [--slow <d>] [--json]

        Runs the findings detectors over each session and prints its header line, then one '!' line per finding, most
        severe first (error, warn, info), then a tally: '+120.4 ! warn slow #21 Work took 251.2ms, …'. Each finding
        cites the stable IDs of the text format (#5, ≋4·4, f18, @0x1BCB). Detectors: slow calls (over --slow, or far
        above their method's p99), cancellation races, orphan results, window and invocation-credit stalls, large
        messages and chunk storms, calls pending and pipes open at the end, protocol errors and failed disconnects,
        schema mismatches, gaps (Drop), ping round-trip outliers; across sessions, reconnect loops.

        Options:
          -s, --session <sel>      A session ID from 'info', 'first', or 'all' (default)
              --slow <d>           A completed call at least this long is slow (default 1s)
              --json               Write ndjson (k = find; n kind, r severity, id IDs, v message) instead of text

        """ + Durations + ExitCodes;
}
