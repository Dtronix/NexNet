using System.Text.Json;
using NexNet.Inspector.Cli;
using NexNet.Inspector.Sample;
using NexNet.Inspector.Tests.Session;

namespace NexNet.Inspector.Tests.Cli;

/// <summary>nexnet-tap findings on the fixtures: text and ndjson, session selection, the slow threshold, errors.</summary>
public class CliFindingsTests
{
    private static async Task<(int Code, string Output, string Error)> RunAsync(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var code = await new TapCli(output, error).RunAsync(args, null, CancellationToken.None);
        return (code, output.ToString(), error.ToString());
    }

    private static string[] Lines(string text) => text.Split('\n', StringSplitOptions.RemoveEmptyEntries);

    [Test]
    public async Task Findings_prints_the_header_the_ranked_findings_and_a_tally()
    {
        var (code, output, error) = await RunAsync("findings", SessionFixtures.Path(FixtureKind.FindingsSlow));
        await Assert.That(code).IsEqualTo(0).Because(error);
        var lines = Lines(output);
        await Assert.That(lines.Length).IsEqualTo(3).Because(output);
        await Assert.That(lines[0]).StartsWith("@0x");
        await Assert.That(lines[0]).Contains("schema FindingsServerNexus#");
        await Assert.That(lines[1]).Contains(" ! warn slow #");
        await Assert.That(lines[2]).IsEqualTo("findings 1: error 0 warn 1 info 0");
    }

    [Test]
    public async Task A_clean_capture_says_findings_none()
    {
        var (code, output, _) = await RunAsync("findings", SessionFixtures.Path(FixtureKind.ChatClient));
        await Assert.That(code).IsEqualTo(0);
        await Assert.That(Lines(output)[^1]).IsEqualTo("findings none");
    }

    [Test]
    public async Task Slow_and_json_options_apply()
    {
        var (code, output, _) = await RunAsync("findings", SessionFixtures.Path(FixtureKind.FindingsSlow), "--slow", "100ms", "--json");
        await Assert.That(code).IsEqualTo(0);
        var records = Lines(output).Select(l => JsonDocument.Parse(l).RootElement).ToArray();
        await Assert.That(records.Select(r => r.GetProperty("k").GetString() ?? "").ToArray()).IsEquivalentTo(["hdr", "find", "stat"]);
        await Assert.That(records[1].GetProperty("v").GetString()).Contains("(slow threshold 100.0ms)");
    }

    [Test]
    public async Task Sessions_can_be_chosen_and_a_missing_one_is_not_found()
    {
        var path = SessionFixtures.Path(FixtureKind.ServerAll);
        var (_, all, _) = await RunAsync("findings", path);
        await Assert.That(Lines(all).Count(l => l.StartsWith("@0x"))).IsEqualTo(3);
        var (_, first, _) = await RunAsync("findings", path, "--session", "first");
        await Assert.That(Lines(first).Count(l => l.StartsWith("@0x"))).IsEqualTo(1);
        var (code, _, error) = await RunAsync("findings", path, "--session", "12345");
        await Assert.That(code).IsEqualTo(4);
        await Assert.That(error).Contains("no session 12345");
    }

    [Test]
    public async Task Findings_needs_a_readable_file()
    {
        var (usage, _, usageError) = await RunAsync("findings");
        await Assert.That(usage).IsEqualTo(2);
        await Assert.That(usageError).Contains("'findings' needs a recording file.");
        var (failed, _, error) = await RunAsync("findings", Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.nntl"));
        await Assert.That(failed).IsEqualTo(1);
        await Assert.That(error).Contains("cannot read");
        await Assert.That(CliArguments.TryParse(["findings", "x.nntl", "--slow", "fast"], null, out _, out var bad)).IsFalse();
        await Assert.That(bad).Contains("is not a duration");
        await Assert.That(CliArguments.TryParse(["findings", "x.nntl", "--slow", "250ms"], null, out var parsed, out _)).IsTrue();
        await Assert.That(parsed!.Slow).IsEqualTo(TimeSpan.FromMilliseconds(250));
    }
}
