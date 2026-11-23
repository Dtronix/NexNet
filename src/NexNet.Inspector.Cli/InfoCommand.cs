using System.Text.Json;
using NexNet.Inspector.Core.Format;

namespace NexNet.Inspector.Cli;

/// <summary><c>nexnet-tap info</c>: summarises a recording as text or JSON.</summary>
internal static class InfoCommand
{
    public static ExitCode Run(TapCli cli, CliArguments args)
    {
        RecordingSummary summary;
        try
        {
            using var stream = new FileStream(args.File!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            summary = RecordingAnalyzer.Analyze(stream, Path.GetFileName(args.File!), args.Messages);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NntlFormatException)
        {
            cli.Error.WriteLine($"nexnet-tap: cannot read {args.File}: {e.Message}");
            return ExitCode.Failed;
        }

        if (args.Json)
            cli.Output.WriteLine(JsonSerializer.Serialize(summary, CliJsonContext.Default.RecordingSummary));
        else
            Write(cli.Output, summary);
        return ExitCode.Ok;
    }

    public static void Write(TextWriter o, RecordingSummary s)
    {
        o.WriteLine($"File       {s.File} ({Text.Count(s.Length)} bytes)");
        o.WriteLine($"Format     v{s.FormatVersion}, written by {s.Producer} {s.ProducerVersion}{(s.Host is null ? "" : " on " + s.Host)}");
        o.WriteLine($"Anchor     {Text.Utc(s.WallClockUtc)}");
        o.WriteLine(s.IndexSource == NntlIndexSource.Footer
            ? $"Index      footer, {Text.Count(s.Blocks)} blocks"
            : $"Index      rebuilt by scan, {Text.Count(s.Blocks)} blocks (no valid footer; 'nexnet-tap repair' writes one)");
        if (s.Damage.Length == 0)
        {
            o.WriteLine("Damage     none");
        }
        else
        {
            long bytes = 0;
            foreach (var d in s.Damage)
                bytes += d.Length;
            o.WriteLine($"Damage     {s.Damage.Length} region(s), {Text.Count(bytes)} bytes");
            foreach (var d in s.Damage)
                o.WriteLine($"           {DamageName(d.Kind)} at offset {Text.Count(d.Offset)}, {Text.Count(d.Length)} bytes");
        }

        o.WriteLine($"Schemas    {(s.Schemas.Length == 0 ? "none" : string.Join(", ", s.Schemas))}");
        o.WriteLine($"Sessions   {s.Sessions.Length}");
        foreach (var session in s.Sessions)
        {
            o.WriteLine();
            WriteSession(o, session);
        }
    }

    private static void WriteSession(TextWriter o, in SessionSummary s)
    {
        var role = s.Role is { } r ? Text.Role(r) : "unknown role (no session-open block)";
        o.WriteLine($"Session {s.Id} ({role}, {s.Transport ?? "unknown transport"})");
        if (s.Endpoint is not null)
            o.WriteLine($"  Endpoint   {s.Endpoint}");
        if (s.LocalEndpoint is not null)
            o.WriteLine($"  Local      {s.LocalEndpoint}");
        if (s.RemoteEndpoint is not null)
            o.WriteLine($"  Remote     {s.RemoteEndpoint}");
        if (s.StartUtc is { } start && s.EndUtc is { } end)
            o.WriteLine($"  Time       {Text.Utc(start)} to {Text.Utc(end)} ({Text.Seconds(s.DurationSeconds)})");
        o.WriteLine(s.FromInception
            ? "  Recorded   from the connection's first byte (preamble present)"
            : s.Snapshot
                ? "  Recorded   from attach, with a state snapshot"
                : "  Recorded   from attach, without a snapshot");
        WriteDirection(o, "Inbound ", s.Inbound);
        WriteDirection(o, "Outbound", s.Outbound);
        o.WriteLine(s.Gaps.Count == 0
            ? "  Gaps       none"
            : $"  Gaps       {Text.Count(s.Gaps.Count)}, {Text.Count(s.Gaps.DroppedOps)} ops / {Text.Count(s.Gaps.DroppedBytes)} bytes dropped");
        o.WriteLine(s.SchemaHash is null
            ? "  Schema     none"
            : $"  Schema     {s.SchemaHash}{(s.SchemaEmbedded ? " (embedded)" : " (not in this file)")}");
        o.WriteLine("  Closed     " + (s.Close is { } c
            ? c.Kind == NntlCloseKind.Disconnected ? $"disconnected ({c.Reason})" : "recording stopped, session still connected"
            : "no close block (the recording ended first)"));
        if (s.Messages is { } m)
        {
            o.WriteLine($"  Messages   {m.Greetings} greetings, {m.Invocations} invocations, {m.AssembledMessages} assembled messages, "
                        + $"{m.Pipes} pipes, {m.ChannelItems} channel items, {m.CollectionMessages} collection messages");
        }
    }

    private static void WriteDirection(TextWriter o, string label, in DirectionSummary d)
    {
        var line = $"  {label}   {Text.Count(d.Ops)} ops, {Text.Count(d.Bytes)} bytes, {Text.Count(d.Frames)} frames";
        if (d.Preamble)
            line += " + preamble";
        if (d.TransportSplitFrames > 0)
            line += $", {Text.Count(d.TransportSplitFrames)} transport-split";
        if (d.RedactedOps > 0)
            line += $", {Text.Count(d.RedactedOps)} redacted ops";
        line += d.Framing switch
        {
            FramingState.EndsMidFrame => "; ends mid-frame",
            FramingState.StoppedAtGap => "; framing stopped at a gap",
            FramingState.Faulted => $"; {Text.Count(d.MalformedRanges)} unframeable range(s)",
            _ => "",
        };
        o.WriteLine(line);
    }

    private static string DamageName(NntlDamageKind kind) => kind switch
    {
        NntlDamageKind.Corrupt => "corrupt",
        NntlDamageKind.Malformed => "malformed block",
        _ => "torn tail",
    };
}
