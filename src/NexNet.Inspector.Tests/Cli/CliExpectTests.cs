using System.Text;
using NexNet.Inspector.Cli;
using NexNet.Inspector.Core.Findings;
using NexNet.Inspector.Core.Model;
using NexNet.Inspector.Sample;
using NexNet.Inspector.Tests.Findings;
using NexNet.Inspector.Tests.Session;
using NexNet.Inspector.Tests.Tap;
using NexNet.Messages;

namespace NexNet.Inspector.Tests.Cli;

/// <summary>
/// nexnet-tap expect: assertions for CI over the fixtures and a live session; exit 0 when every expectation held, 5
/// when one failed, the findings report first, one line per expectation citing IDs, the verdict last.
/// </summary>
public class CliExpectTests
{
    private static async Task<(int Code, string Output, string Error)> RunAsync(params string[] args)
    {
        var output = new StringWriter { NewLine = "\n" };
        var error = new StringWriter();
        var code = await new TapCli(output, error).RunAsync(args, null, CancellationToken.None);
        return (code, output.ToString(), error.ToString());
    }

    private static string[] Lines(string text) => text.Split('\n', StringSplitOptions.RemoveEmptyEntries);

    [Test]
    public async Task A_clean_capture_meets_every_expectation_and_exits_0()
    {
        var (code, output, error) = await RunAsync("expect", SessionFixtures.Path(FixtureKind.ChatClient),
            "--no-gaps", "--no-errors", "--p99", "*<1s", "--max-findings", "high=0,all=0");
        await Assert.That(code).IsEqualTo((int)ExitCode.Ok).Because(error + output);
        var lines = Lines(output);
        await Assert.That(lines[0]).StartsWith("@0x");
        await Assert.That(lines).Contains("findings none");
        await Assert.That(lines.Count(l => l.StartsWith("expect ok ", StringComparison.Ordinal))).IsEqualTo(5).Because(output);
        await Assert.That(lines).Contains("expect ok no-gaps: gaps 0");
        await Assert.That(lines).Contains("expect ok no-errors: errors 0");
        await Assert.That(lines.Single(l => l.StartsWith("expect ok p99", StringComparison.Ordinal))).Matches("^expect ok p99 \\*<1s: p99 .+ over [0-9]+ calls \\(slowest #[0-9]+ \\w+\\)$");
        await Assert.That(lines[^1]).IsEqualTo("expect pass: 5 of 5 held");
    }

    [Test]
    public async Task Gaps_fail_with_exit_5_and_cite_where()
    {
        var (code, output, _) = await RunAsync("expect", SessionFixtures.Path(FixtureKind.ChatServer), "--no-gaps", "--no-errors");
        await Assert.That(code).IsEqualTo(5);
        var lines = Lines(output);
        await Assert.That(lines.Single(l => l.Contains("no-gaps", StringComparison.Ordinal))).Matches("^expect FAIL no-gaps: gaps [1-9][0-9]*, [0-9]+ ops .+ dropped \\(\\+[0-9.]+ (in|out) [0-9]+ ops");
        await Assert.That(lines).Contains("expect ok no-errors: errors 0");
        await Assert.That(lines[^1]).IsEqualTo("expect FAIL: 1 of 2 failed");
        // The findings report says why too: gaps are findings.
        await Assert.That(output).Contains(" ! warn gap ");
    }

    [Test]
    public async Task P99_bounds_and_finding_limits_judge_the_slow_fixture()
    {
        var (code, output, _) = await RunAsync("expect", SessionFixtures.Path(FixtureKind.FindingsSlow),
            "--p99", "Work<100ms,Work<=1s,Missing*<1s", "--max-findings", "warn=0,slow=1,error=0");
        await Assert.That(code).IsEqualTo(5);
        var lines = Lines(output);
        await Assert.That(lines).Contains(l => l.StartsWith("expect FAIL p99 Work<100ms: p99 2", StringComparison.Ordinal) && l.Contains("over 16 calls (slowest #"));
        await Assert.That(lines).Contains(l => l.StartsWith("expect ok p99 Work<=1s: ", StringComparison.Ordinal));
        await Assert.That(lines).Contains("expect FAIL p99 Missing*<1s: no completed calls of Missing*");
        await Assert.That(lines).Contains(l => l.StartsWith("expect FAIL max-findings warn=0: warn or worse 1, at most 0 (warn slow #", StringComparison.Ordinal));
        await Assert.That(lines).Contains(l => l.StartsWith("expect ok max-findings slow=1: slow 1, at most 1", StringComparison.Ordinal));
        await Assert.That(lines).Contains("expect ok max-findings error=0: error 0, at most 0");
        await Assert.That(lines[^1]).IsEqualTo("expect FAIL: 3 of 6 failed");
    }

    [Test]
    public async Task Errors_fail_no_errors_and_high_findings()
    {
        var crafted = new CraftedSession().Handshake()
            .Raw(TapDirection.Inbound, 5, [0xEE, 0xEE, 0xEE, 0xEE])
            .Close(10, DisconnectReason.ProtocolError);
        await Assert.That(CliArguments.TryParse(["expect", "x.nntl", "--no-errors", "--max-findings", "high=0"], null, out var args, out var problem))
            .IsTrue().Because(problem ?? "");
        var output = new StringWriter { NewLine = "\n" };
        var code = ExpectCommand.Check(output, args!, [crafted.Session]);
        var lines = Lines(output.ToString());
        await Assert.That(code).IsEqualTo(ExitCode.ExpectationFailed);
        await Assert.That(lines).Contains(l => l.StartsWith("expect FAIL no-errors: errors ", StringComparison.Ordinal) && l.Contains(": f") && l.Contains("END"))
            .Because(output.ToString());
        await Assert.That(lines).Contains(l => l.StartsWith("expect FAIL max-findings high=0: error ", StringComparison.Ordinal) && l.Contains("(error "));
        await Assert.That(lines[^1]).IsEqualTo("expect FAIL: 2 of 2 failed");
    }

    [Test]
    public async Task The_command_line_is_checked()
    {
        await Assert.That(CliArguments.TryParse(["expect", "a.nntl", "--p99", "Send*<50ms,Join<=1.5s", "--max-findings", "high=0,medium=2,low=9,window-stall=1,all=4,7"],
            null, out var parsed, out var problem)).IsTrue().Because(problem ?? "");
        await Assert.That(parsed!.Latencies.Length).IsEqualTo(2);
        await Assert.That(parsed.Latencies[0]).IsEqualTo(new ExpectLatency("Send*", 50, false, "Send*<50ms"));
        await Assert.That(parsed.Latencies[1]).IsEqualTo(new ExpectLatency("Join", 1500, true, "Join<=1.5s"));
        var limits = parsed.FindingLimits;
        await Assert.That(limits.Select(l => (l.Scope, l.Severity, l.Max)).Take(3).ToArray()).IsEquivalentTo(new[]
        {
            (ExpectFindingScope.Severity, FindingSeverity.Error, 0), (ExpectFindingScope.Severity, FindingSeverity.Warning, 2),
            (ExpectFindingScope.Severity, FindingSeverity.Info, 9),
        });
        await Assert.That(limits[3].Kind).IsEqualTo(FindingKind.WindowStall);
        await Assert.That(limits[4].Scope).IsEqualTo(ExpectFindingScope.All);
        await Assert.That(limits[5]).IsEqualTo(new ExpectFindingLimit(ExpectFindingScope.All, FindingSeverity.Info, default, 7, "7"));

        // An endpoint as the argument is a live expect.
        await Assert.That(CliArguments.TryParse(["expect", "tcp://127.0.0.1:5000", "--token", "t", "--no-gaps"], null, out var live, out problem))
            .IsTrue().Because(problem ?? "");
        await Assert.That(live!.Has(CliOption.Endpoint)).IsTrue();
        await Assert.That(live.Positionals.Count).IsEqualTo(0);

        (string[] Args, string Error)[] bad =
        [
            (["expect", "a.nntl"], "needs at least one expectation"),
            (["expect", "--no-gaps"], "needs a recording file or an endpoint"),
            (["expect", "a.nntl", "--p99", "Send*50ms"], "is not a method and a bound"),
            (["expect", "a.nntl", "--p99", "<50ms"], "is not a method and a bound"),
            (["expect", "a.nntl", "--max-findings", "severe=0"], "is not a limit"),
            (["expect", "a.nntl", "--max-findings", "high=-1"], "is not a limit"),
            (["expect", "a.nntl", "--no-gaps", "--duration", "1s"], "apply to a live endpoint"),
            (["expect", "tcp://127.0.0.1:5000", "--no-gaps"], "No endpoint token"),
            (["expect", "tcp://127.0.0.1:5000", "--token", "t", "--no-gaps", "--session", "all"], "checks one session"),
        ];
        foreach (var (args, error) in bad)
        {
            await Assert.That(CliArguments.TryParse(args, null, out _, out var message)).IsFalse().Because(string.Join(' ', args));
            await Assert.That(message).Contains(error);
        }

        var (code, _, usage) = await RunAsync("expect", "a.nntl");
        await Assert.That(code).IsEqualTo((int)ExitCode.Usage);
        await Assert.That(usage).Contains("needs at least one expectation");
        var (missing, _, missingError) = await RunAsync("expect", Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.nntl"), "--no-gaps");
        await Assert.That(missing).IsEqualTo((int)ExitCode.Failed);
        await Assert.That(missingError).Contains("cannot read");
        var (_, help, _) = await RunAsync("help", "expect");
        await Assert.That(help).Contains("5    an expectation failed");
    }

    [Test]
    public async Task A_live_session_is_attached_for_its_duration_and_checked()
    {
        await using var h = new TapHarness(TapTransport.Tcp);
        await h.StartAsync();
        await h.StartEndpointAsync(TapTransport.Tcp);
        var client = await h.ConnectClientAsync();
        await h.WaitForSessionAsync();

        using var stop = new CancellationTokenSource();
        var calls = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                await client.Proxy.Echo([1, 2, 3]);
                await Task.Delay(20);
            }
        });
        var (code, output, error) = await RunAsync("expect", h.EndpointAddress!, "--token", Encoding.UTF8.GetString(TapHarness.EndpointToken),
            "--duration", "600ms", "--no-errors", "--p99", "Echo<10s", "--max-findings", "error=0");
        await stop.CancelAsync();
        await calls;

        await Assert.That(code).IsEqualTo((int)ExitCode.Ok).Because(error + output);
        var lines = Lines(output);
        await Assert.That(lines[0]).Contains("server tcp");
        await Assert.That(lines).Contains(l => l.StartsWith("expect ok p99 Echo<10s: p99 ", StringComparison.Ordinal));
        await Assert.That(lines[^1]).IsEqualTo("expect pass: 3 of 3 held");
    }
}
