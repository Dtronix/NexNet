using System.Globalization;
using System.Text;
using NexNet.Inspector.Core.Session;
using NexNet.Inspector.Core.Text;

namespace NexNet.Inspector.Core.Findings;

/// <summary>
/// Findings in the text format (docs/internals/inspector-text-format.md): one `!` line per finding, written through the
/// shared <see cref="TextRecordWriter"/> as text or ndjson (`k` = `find`).
/// </summary>
public static class FindingsText
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    /// <summary>The short code of a kind (`slow`, `window-stall`, …), as the text form and ndjson `n` write it.</summary>
    public static string Code(FindingKind kind) => kind switch
    {
        FindingKind.ProtocolError => "protocol",
        FindingKind.Disconnect => "disconnect",
        FindingKind.SchemaMismatch => "schema",
        FindingKind.Gap => "gap",
        FindingKind.OrphanResult => "orphan",
        FindingKind.CancelRace => "cancel-race",
        FindingKind.PendingAtEnd => "pending",
        FindingKind.PipeOpenAtEnd => "pipe-open",
        FindingKind.WindowStall => "window-stall",
        FindingKind.CreditStall => "credit-stall",
        FindingKind.SlowCall => "slow",
        FindingKind.PingRtt => "ping-rtt",
        FindingKind.LargeMessage => "large",
        FindingKind.ChunkStorm => "chunk-storm",
        _ => "reconnect-loop",
    };

    /// <summary>The word of a severity: `error`, `warn`, `info` (ndjson `r`).</summary>
    public static string Word(FindingSeverity severity) => severity switch
    {
        FindingSeverity.Error => "error",
        FindingSeverity.Warning => "warn",
        _ => "info",
    };

    /// <summary>
    /// The record of a finding: kind <see cref="TextRecordKind.Finding"/>, the severity word as its outcome, the kind code
    /// as its name, the IDs (space-separated) as its ID and the message as its value.
    /// </summary>
    /// <param name="finding">The finding.</param>
    public static TextRecord ToRecord(in Finding finding) => new()
    {
        Kind = TextRecordKind.Finding,
        Ticks = finding.Range.Start,
        LastTicks = finding.Range.End,
        TimeMs = finding.StartMs,
        SpanMs = Math.Max(0, finding.EndMs - finding.StartMs),
        Id = finding.Ids.Length > 0 ? finding.IdText : null,
        Name = Code(finding.Kind),
        Outcome = Word(finding.Severity),
        Value = finding.Message,
        Count = 1,
        Bytes = -1,
        Frame = -1,
        Entity = -1,
    };

    /// <summary>The text line of a finding, without newline: `+120.4 ! warn slow #21 Work took 251.2ms, …`.</summary>
    /// <param name="finding">The finding.</param>
    public static string Format(in Finding finding)
    {
        var sb = new StringBuilder();
        TextRecordWriter.AppendText(sb, ToRecord(finding));
        return sb.ToString();
    }

    /// <summary>The tally line: `findings 3: error 0 warn 2 info 1`, or `findings none`.</summary>
    /// <param name="findings">The findings counted.</param>
    public static TextRecord Tally(IReadOnlyList<Finding> findings)
    {
        ArgumentNullException.ThrowIfNull(findings);
        int errors = 0, warnings = 0, infos = 0;
        foreach (var finding in findings)
        {
            switch (finding.Severity)
            {
                case FindingSeverity.Error:
                    errors++;
                    break;
                case FindingSeverity.Warning:
                    warnings++;
                    break;
                default:
                    infos++;
                    break;
            }
        }

        return new TextRecord
        {
            Kind = TextRecordKind.Stat,
            Value = findings.Count == 0
                ? "findings none"
                : string.Create(Invariant, $"findings {findings.Count}: error {errors} warn {warnings} info {infos}"),
            Count = 1,
            Bytes = -1,
            Frame = -1,
            Entity = -1,
        };
    }

    /// <summary>
    /// The findings report (`nexnet-tap findings`, `expect`, the test helper): per session its header line, its ranked
    /// findings and a tally (text puts a blank line between sessions), then, when there are any, the findings across
    /// sessions (reconnect loops) after an `across N sessions` line.
    /// </summary>
    /// <param name="output">Where lines go.</param>
    /// <param name="sessions">The sessions, indexed.</param>
    /// <param name="options">Detector thresholds; <see cref="FindingsOptions.Default"/> when null.</param>
    /// <param name="text">The format; <see cref="TextOptions.Default"/> when null.</param>
    /// <returns>Every finding reported, in report order.</returns>
    public static List<Finding> WriteReport(TextWriter output, IReadOnlyList<CaptureSession> sessions, FindingsOptions? options = null,
        TextOptions? text = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(sessions);
        text ??= TextOptions.Default;
        var writer = new TextRecordWriter(text);
        var all = new List<Finding>();
        for (var i = 0; i < sessions.Count; i++)
        {
            if (i > 0 && text.Format == TextFormat.Text)
                output.Write('\n');
            writer.Measure(new TextQuery(sessions[i], text).Header());
            writer.Write(output);
            var findings = FindingsEngine.Analyze(sessions[i], options);
            Write(output, findings, text, tally: true);
            all.AddRange(findings);
        }

        var across = FindingsEngine.AnalyzeAcross(sessions, options);
        if (across.Count > 0)
        {
            if (text.Format == TextFormat.Text)
                output.Write('\n');
            writer.Measure(new TextRecord
            {
                Kind = TextRecordKind.Stat,
                Value = string.Create(Invariant, $"across {sessions.Count} sessions"),
                Bytes = -1,
                Frame = -1,
                Entity = -1,
            });
            writer.Write(output);
            Write(output, across, text);
            all.AddRange(across);
        }

        return all;
    }

    /// <summary>Writes findings, one line each, in the given order, as text or ndjson (<see cref="TextOptions.Format"/>).</summary>
    /// <param name="output">Where lines go.</param>
    /// <param name="findings">The findings (rank them first with <see cref="FindingsEngine.Rank"/> for a report).</param>
    /// <param name="options">The format; <see cref="TextOptions.Default"/> when null.</param>
    /// <param name="tally">Also write the tally line after them.</param>
    /// <returns>Lines written.</returns>
    public static int Write(TextWriter output, IReadOnlyList<Finding> findings, TextOptions? options = null, bool tally = false)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(findings);
        var writer = new TextRecordWriter(options ?? TextOptions.Default);
        foreach (var finding in findings)
        {
            writer.Measure(ToRecord(finding));
            writer.Write(output);
        }

        if (!tally)
            return findings.Count;
        writer.Measure(Tally(findings));
        writer.Write(output);
        return findings.Count + 1;
    }
}
