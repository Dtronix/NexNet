using System.Text.Json;
using NexNet.Inspector.Core.Findings;
using NexNet.Inspector.Core.Model;
using NexNet.Inspector.Core.Text;

namespace NexNet.Inspector.Tests.Findings;

/// <summary>Findings in the text format: the `!` line, ndjson `find` records and the tally, through the shared writer.</summary>
public class FindingsTextTests
{
    private static Finding Sample(FindingSeverity severity = FindingSeverity.Warning, string[]? ids = null) =>
        new(severity, FindingKind.SlowCall, "Work took 251.2ms; its other 21 calls: median 1.0ms, p99 1.4ms", ids ?? ["#21"],
            new TimeWindow(120_400, 371_600))
        {
            SessionId = 1,
            StartMs = 120.4,
            EndMs = 371.6,
        };

    [Test]
    public async Task A_finding_is_one_bang_line_with_severity_kind_ids_and_message()
    {
        await Assert.That(FindingsText.Format(Sample()))
            .IsEqualTo("+120.4 ! warn slow #21 Work took 251.2ms; its other 21 calls: median 1.0ms, p99 1.4ms");
        await Assert.That(FindingsText.Format(Sample(FindingSeverity.Error, ["⇄1·2", "f12"])))
            .StartsWith("+120.4 ! error slow ⇄1·2 f12 Work took");
        await Assert.That(FindingsText.Format(Sample(FindingSeverity.Info, []))).StartsWith("+120.4 ! info slow Work took");
        await Assert.That(TextRecordWriter.Code(TextRecordKind.Finding)).IsEqualTo("find");
    }

    [Test]
    public async Task Ndjson_findings_carry_kind_severity_ids_message_and_span()
    {
        using var output = new StringWriter();
        var lines = FindingsText.Write(output, [Sample(), Sample(FindingSeverity.Info, ["#3", "#4"])], new TextOptions { Format = TextFormat.Ndjson }, tally: true);
        await Assert.That(lines).IsEqualTo(3);
        var records = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => JsonDocument.Parse(l).RootElement).ToArray();
        var first = records[0];
        await Assert.That(first.GetProperty("k").GetString()).IsEqualTo("find");
        await Assert.That(first.GetProperty("t").GetDouble()).IsEqualTo(120.4);
        await Assert.That(first.GetProperty("n").GetString()).IsEqualTo("slow");
        await Assert.That(first.GetProperty("r").GetString()).IsEqualTo("warn");
        await Assert.That(first.GetProperty("id").GetString()).IsEqualTo("#21");
        await Assert.That(first.GetProperty("sp").GetDouble()).IsEqualTo(251.2);
        await Assert.That(first.GetProperty("v").GetString()).StartsWith("Work took");
        await Assert.That(records[1].GetProperty("id").GetString()).IsEqualTo("#3 #4");
        await Assert.That(records[2].GetProperty("k").GetString()).IsEqualTo("stat");
        await Assert.That(records[2].GetProperty("v").GetString()).IsEqualTo("findings 2: error 0 warn 1 info 1");
    }

    [Test]
    public async Task The_tally_counts_each_severity()
    {
        await Assert.That(FindingsText.Tally([]).Value).IsEqualTo("findings none");
        var tally = FindingsText.Tally([Sample(FindingSeverity.Error), Sample(), Sample(), Sample(FindingSeverity.Info)]);
        await Assert.That(tally.Value).IsEqualTo("findings 4: error 1 warn 2 info 1");
        await Assert.That(TextLegend.Text).Contains("! warn slow #21");
    }

    [Test]
    public async Task Every_kind_has_its_own_code()
    {
        var kinds = Enum.GetValues<FindingKind>();
        var codes = kinds.Select(FindingsText.Code).ToArray();
        await Assert.That(codes.Distinct().Count()).IsEqualTo(kinds.Length);
        await Assert.That(codes.All(c => c.Length > 0 && !c.Contains(' '))).IsTrue();
    }
}
