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
          nexnet-tap help [command]
          nexnet-tap --version

        Commands:
          list     Show the inspected server's (or client's) live sessions and its recording policy.
          record   Record one session, the first one, or all sessions (new ones from their first byte) into a .nntl
                   file until Ctrl+C, --duration, or the session closes.
          info     Summarise a recording: header, index, damage, and per session times, ops, bytes, frames, gaps,
                   schema and close reason.
          repair   Rebuild the index footer of a crashed or truncated recording, in place.

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

    internal static readonly string RepairUsage = """
        Usage: nexnet-tap repair <file.nntl> [--force]

        Rebuilds the index footer in place: cuts off a torn tail and any old footer, then writes a fresh index. A file
        whose footer is intact is left untouched unless --force.

        Options:
          -f, --force              Rebuild even when the footer is intact

        """ + ExitCodes;
}
