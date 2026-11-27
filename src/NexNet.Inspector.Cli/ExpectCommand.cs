using System.Globalization;
using System.Text;
using NexNet.Inspector.Core.Findings;
using NexNet.Inspector.Core.Format;
using NexNet.Inspector.Core.Session;
using NexNet.Inspector.Core.Text;

namespace NexNet.Inspector.Cli;

/// <summary>
/// <c>nexnet-tap expect</c>: assertions over a recording or a live session, for CI. Prints the findings report
/// (<see cref="FindingsText.WriteReport"/>), then one <c>expect ok|FAIL</c> line per expectation with what was measured
/// and the IDs that broke it, then the verdict; exits 0 when every expectation held, 5 when one failed.
/// </summary>
internal static class ExpectCommand
{
    /// <summary>Options <c>expect</c> accepts.</summary>
    public const CliOption Options = CliCommands.ConnectOptions | CliOption.Session | CliOption.Duration | CliOption.NoSnapshot | CliOption.Slow
                                     | CliArguments.ExpectOptions;

    private const int Cited = 3;
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    /// <summary>Runs <c>expect</c>.</summary>
    public static async Task<ExitCode> RunAsync(TapCli cli, CliArguments args, CancellationToken cancellationToken)
    {
        RecordingFile? recording = null;
        List<CaptureSession> sessions;
        if (args.Has(CliOption.Endpoint))
        {
            var (session, code) = await QueryCommand.AttachAsync(cli, args, cancellationToken).ConfigureAwait(false);
            if (session is null)
                return code;
            sessions = [session];
        }
        else
        {
            ExitCode opened;
            try
            {
                opened = QueryCommand.Open(cli, args.File!, args, out recording, out sessions);
            }
            catch (Exception e) when (e is IOException or NntlFormatException)
            {
                await cli.Error.WriteLineAsync($"nexnet-tap: cannot read {args.File}: {e.Message}").ConfigureAwait(false);
                return ExitCode.Failed;
            }

            if (opened != ExitCode.Ok)
                return opened;
        }

        using (recording)
            return Check(cli.Output, args, sessions);
    }

    /// <summary>Checks the expectations of <paramref name="args"/> over <paramref name="sessions"/> and writes the report.</summary>
    /// <returns><see cref="ExitCode.Ok"/> when every expectation held; <see cref="ExitCode.ExpectationFailed"/> otherwise.</returns>
    public static ExitCode Check(TextWriter output, CliArguments args, IReadOnlyList<CaptureSession> sessions)
    {
        var options = args.Has(CliOption.Slow) ? new FindingsOptions { SlowCallMs = args.Slow.TotalMilliseconds } : FindingsOptions.Default;
        var findings = FindingsText.WriteReport(output, sessions, options);
        output.Write('\n');

        var tagged = sessions.Count > 1;
        var checks = 0;
        var failed = 0;
        void Report(bool ok, string what, string measured)
        {
            checks++;
            if (!ok)
                failed++;
            output.WriteLine((ok ? "expect ok " : "expect FAIL ") + what + ": " + measured);
        }

        if (args.Has(CliOption.NoGaps))
        {
            var (count, text) = Gaps(sessions, tagged);
            Report(count == 0, "no-gaps", text);
        }

        if (args.Has(CliOption.NoErrors))
        {
            var (count, text) = Errors(sessions, tagged);
            Report(count == 0, "no-errors", text);
        }

        foreach (var latency in args.Latencies)
        {
            var (ok, text) = Latency(sessions, latency, tagged);
            Report(ok, "p99 " + latency.Text, text);
        }

        foreach (var limit in args.FindingLimits)
        {
            var (ok, text) = Findings(findings, limit, tagged);
            Report(ok, "max-findings " + limit.Text, text);
        }

        output.WriteLine(failed == 0
            ? string.Create(Invariant, $"expect pass: {checks} of {checks} held")
            : string.Create(Invariant, $"expect FAIL: {failed} of {checks} failed"));
        return failed == 0 ? ExitCode.Ok : ExitCode.ExpectationFailed;
    }

    private static (int Count, string Text) Gaps(IReadOnlyList<CaptureSession> sessions, bool tagged)
    {
        var sb = new StringBuilder();
        var count = 0;
        long ops = 0, bytes = 0;
        foreach (var session in sessions)
        {
            lock (session.Gate)
            {
                var frequency = Math.Max(1, session.Header.StopwatchFrequency);
                foreach (var gap in session.Gaps)
                {
                    if (count++ < Cited)
                    {
                        sb.Append(count == 1 ? " (" : ", ");
                        Tag(sb, session, tagged)
                            .Append(Invariant, $"+{Core.Model.TimeWindow.MillisecondsFromTicks(gap.FromTicks - session.StartTicks, frequency):0.0} ")
                            .Append(gap.Direction == Core.Model.TapDirection.Inbound ? "in " : "out ")
                            .Append(Invariant, $"{gap.DroppedOps} ops");
                    }

                    ops += gap.DroppedOps;
                    bytes += gap.DroppedBytes;
                }
            }
        }

        if (count > Cited)
            sb.Append(Invariant, $", …+{count - Cited}");
        if (count > 0)
            sb.Append(')');
        var head = new StringBuilder(string.Create(Invariant, $"gaps {count}"));
        if (count > 0)
            TextValues.AppendSize(head.Append(Invariant, $", {ops} ops "), bytes).Append(" dropped");
        return (count, head.Append(sb).ToString());
    }

    private static (int Count, string Text) Errors(IReadOnlyList<CaptureSession> sessions, bool tagged)
    {
        var sb = new StringBuilder();
        var count = 0;
        var errors = new List<TextRecord>();
        foreach (var session in sessions)
        {
            errors.Clear();
            new TextRenderer(session, new TextOptions
            {
                Level = TextLevel.Messages,
                Header = false,
                Filter = TextFilter.All with { Match = static r => TextQuery.IsError(r), MatchKey = "errors" },
            }).Collect(errors);
            foreach (var e in errors)
            {
                if (count++ >= Cited)
                    continue;
                Tag(sb.Append(count == 1 ? ": " : ", "), session, tagged).Append(e.Id ?? "END");
                if (e.Name is not null && e.Kind is TextRecordKind.Call or TextRecordKind.Result)
                    sb.Append(' ').Append(e.Name);
                if ((e.Kind is TextRecordKind.Problem or TextRecordKind.Pipe ? e.Value : e.Outcome) is { } what)
                    sb.Append(' ').Append(what);
            }
        }

        if (count > Cited)
            sb.Append(Invariant, $", …+{count - Cited}");
        return (count, string.Create(Invariant, $"errors {count}") + sb);
    }

    private static (bool Ok, string Text) Latency(IReadOnlyList<CaptureSession> sessions, ExpectLatency latency, bool tagged)
    {
        var durations = new List<double>();
        var calls = new List<TextRecord>();
        double slowest = -1;
        string? slowestId = null;
        foreach (var session in sessions)
        {
            calls.Clear();
            var method = latency.Method;
            new TextRenderer(session, new TextOptions
            {
                Level = TextLevel.Messages,
                Collapse = false,
                Header = false,
                Filter = TextFilter.All with
                {
                    Match = r => r.Kind is TextRecordKind.Call or TextRecordKind.Result && r.DurationMs is not null && r.Name is { } name
                                 && TailFilter.Glob(method, name),
                    MatchKey = "p99 " + method,
                },
            }).Collect(calls);
            var seen = new HashSet<int>();
            foreach (var call in calls)
            {
                if (call.Entity >= 0 && !seen.Add(call.Entity))
                    continue;
                var ms = call.DurationMs!.Value;
                durations.Add(ms);
                if (ms > slowest)
                {
                    slowest = ms;
                    slowestId = Tag(new StringBuilder(), session, tagged).Append(call.Id).Append(' ').Append(call.Name).ToString();
                }
            }
        }

        if (durations.Count == 0)
            return (false, "no completed calls of " + latency.Method);
        durations.Sort();
        var p99 = durations[Math.Clamp((int)Math.Ceiling(0.99 * durations.Count) - 1, 0, durations.Count - 1)];
        var ok = latency.Inclusive ? p99 <= latency.Ms : p99 < latency.Ms;
        var sb = new StringBuilder("p99 ");
        TextValues.AppendDuration(sb, p99);
        TextValues.AppendDuration(sb.Append(" max "), slowest);
        sb.Append(Invariant, $" over {durations.Count} calls");
        if (slowestId is not null)
            sb.Append(" (slowest ").Append(slowestId).Append(')');
        return (ok, sb.ToString());
    }

    private static (bool Ok, string Text) Findings(List<Finding> findings, ExpectFindingLimit limit, bool tagged)
    {
        var sb = new StringBuilder();
        var count = 0;
        foreach (var finding in findings)
        {
            var counted = limit.Scope switch
            {
                ExpectFindingScope.Severity => finding.Severity >= limit.Severity,
                ExpectFindingScope.Kind => finding.Kind == limit.Kind,
                _ => true,
            };
            if (!counted || count++ >= Cited)
                continue;
            sb.Append(count == 1 ? " (" : ", ").Append(FindingsText.Word(finding.Severity)).Append(' ').Append(FindingsText.Code(finding.Kind));
            if (tagged)
                sb.Append(Invariant, $" @0x{finding.SessionId:X}");
            if (finding.Ids.Length > 0)
                sb.Append(' ').Append(finding.Ids[0]);
        }

        if (count > Cited)
            sb.Append(Invariant, $", …+{count - Cited}");
        if (count > 0)
            sb.Append(')');
        var what = limit.Scope switch
        {
            ExpectFindingScope.Severity => FindingsText.Word(limit.Severity) + (limit.Severity == FindingSeverity.Error ? "" : " or worse"),
            ExpectFindingScope.Kind => FindingsText.Code(limit.Kind),
            _ => "findings",
        };
        return (count <= limit.Max, string.Create(Invariant, $"{what} {count}, at most {limit.Max}") + sb);
    }

    private static StringBuilder Tag(StringBuilder sb, CaptureSession session, bool tagged) =>
        tagged ? sb.Append(Invariant, $"@0x{session.SessionId:X} ") : sb;

    internal static readonly string Usage = """
        Usage: nexnet-tap expect <file.nntl | endpoint> [expectations] [options]

        Checks a recording (every session, or --session's) or a live session (attached for --duration, default 2s) for
        CI: prints the findings report ('help findings'), then one 'expect ok|FAIL <expectation>: <measured>' line per
        expectation citing the IDs that broke it (#5, f18, @0x1BCB), then 'expect pass' or 'expect FAIL'.

        Expectations (at least one):
              --no-gaps            The capture dropped nothing (no gap blocks: Drop overflow)
              --no-errors          No call failed (exception, unauthorized, refused), no bad frame, no refused pipe, and
                                   every disconnect was graceful (what 'errors' lists)
              --p99 <m<d,…>        The p99 duration of completed calls of each method (a glob: Send*, *) is under the
                                   bound: 'Send*<50ms,Join<=1s' (quote it: < is a shell redirect); a method with no
                                   completed call fails
              --max-findings <l,…> At most N findings: error=0 (also high), warn=2 (medium; counts warn and error),
                                   info=5 (low; every finding), a kind (slow=0, gap=0, window-stall=1, …) or all=10
        Options:
          -s, --session <sel>      A session ID, 'first', or 'all' (file default: all; live: one, default the first)
              --slow <d>           The findings' slow-call threshold (default 1s)
          -d, --duration <d>       Live: how long to attach (default 2s)
              --no-snapshot        Live: attach without a state snapshot

        """ + """
          -e, --endpoint <addr>    Inspector endpoint (or give it as the argument): tcp://, tls:// or uds:
          -t, --token <token>      Endpoint token (default: the NEXNET_TAP_TOKEN environment variable)
              --insecure, --server-name <name>, --connect-timeout <d>  as for 'record'

        Exit codes:
          0    every expectation held
          1    failure: endpoint unreachable or token refused, I/O error, unreadable file
          2    invalid command line
          4    session not found
          5    an expectation failed

        """;
}
