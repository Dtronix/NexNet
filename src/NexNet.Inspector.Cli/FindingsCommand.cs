using NexNet.Inspector.Core.Findings;
using NexNet.Inspector.Core.Format;
using NexNet.Inspector.Core.Session;
using NexNet.Inspector.Core.Text;

namespace NexNet.Inspector.Cli;

/// <summary>
/// <c>nexnet-tap findings</c>: runs the findings detectors (Core <see cref="FindingsEngine"/>) over a recording's sessions
/// and prints, per session, its header line, the ranked findings and a tally, then the findings across sessions
/// (reconnect loops), in the text format or ndjson.
/// </summary>
internal static class FindingsCommand
{
    public static ExitCode Run(TapCli cli, CliArguments args)
    {
        RecordingFile recording;
        try
        {
            recording = RecordingFile.Open(args.File!);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NntlFormatException)
        {
            cli.Error.WriteLine($"nexnet-tap: cannot read {args.File}: {e.Message}");
            return ExitCode.Failed;
        }

        using (recording)
        {
            var selected = new List<NntlSessionOpen>();
            foreach (var open in recording.Sessions)
            {
                if (!args.Has(CliOption.Session) || args.Selector == SessionSelectorKind.All
                    || (args.Selector == SessionSelectorKind.First && selected.Count == 0)
                    || (args.Selector == SessionSelectorKind.Id && open.SessionId == args.SessionId))
                    selected.Add(open);
            }

            if (selected.Count == 0)
            {
                cli.Error.WriteLine(args.Selector == SessionSelectorKind.Id
                    ? $"nexnet-tap: {args.File} has no session {args.SessionId}."
                    : $"nexnet-tap: {args.File} has no sessions.");
                return ExitCode.NotFound;
            }

            var options = args.Has(CliOption.Slow) ? new FindingsOptions { SlowCallMs = args.Slow.TotalMilliseconds } : FindingsOptions.Default;
            var text = new TextOptions { Format = args.Json ? TextFormat.Ndjson : TextFormat.Text };
            var writer = new TextRecordWriter(text);
            var sessions = new List<CaptureSession>();
            try
            {
                foreach (var open in selected)
                {
                    var session = recording.CreateSession(open.SessionId);
                    recording.Index(session);
                    sessions.Add(session);
                    if (sessions.Count > 1 && !args.Json)
                        cli.Output.Write('\n');
                    if (Header(session) is { } header)
                    {
                        writer.Measure(header);
                        writer.Write(cli.Output);
                    }

                    FindingsText.Write(cli.Output, FindingsEngine.Analyze(session, options), text, tally: true);
                }
            }
            catch (Exception e) when (e is IOException or NntlFormatException)
            {
                cli.Error.WriteLine($"nexnet-tap: cannot read {args.File}: {e.Message}");
                return ExitCode.Failed;
            }

            var across = FindingsEngine.AnalyzeAcross(sessions, options);
            if (across.Count > 0)
            {
                if (!args.Json)
                    cli.Output.Write('\n');
                writer.Measure(new TextRecord { Kind = TextRecordKind.Stat, Value = $"across {sessions.Count} sessions", Bytes = -1, Frame = -1, Entity = -1 });
                writer.Write(cli.Output);
                FindingsText.Write(cli.Output, across, text);
            }
        }

        return ExitCode.Ok;
    }

    // The session's header line, from the text renderer.
    private static TextRecord? Header(CaptureSession session)
    {
        var records = new List<TextRecord>();
        new TextRenderer(session, new TextOptions
        {
            Level = TextLevel.Summary,
            Filter = new TextFilter { Match = static r => r.Kind == TextRecordKind.Header, MatchKey = "header" },
        }).Collect(records);
        return records.Count > 0 ? records[0] : null;
    }
}
