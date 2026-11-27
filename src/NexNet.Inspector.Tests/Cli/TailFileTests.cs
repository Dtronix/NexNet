using System.Runtime.CompilerServices;
using System.Text;
using NexNet.Inspector.Cli;
using NexNet.Inspector.Core.Text;
using NexNet.Inspector.Sample;
using NexNet.Inspector.Tests.Session;
using NexNet.Inspector.Tests.Text;

namespace NexNet.Inspector.Tests.Cli;

/// <summary>nexnet-tap tail over the fixtures: filters and budgets (goldens), cursors, --until, --follow.</summary>
public class TailFileTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    internal static async Task<(int Code, string Output, string Error)> RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        var output = new StringWriter { NewLine = "\n" };
        var error = new StringWriter();
        var code = await new TapCli(output, error).RunAsync(args, null, cancellationToken);
        return (code, output.ToString(), error.ToString());
    }

    private static string Fixture(FixtureKind kind) => SessionFixtures.Path(kind);

    private static string GoldenDirectory([CallerFilePath] string source = "")
        => Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(source))!, "Golden", "Tail");

    public static IEnumerable<(string Name, FixtureKind Kind, string Options)> GoldenCases()
    {
        yield return ("chat-server.method", FixtureKind.ChatServer, "--method Send*");
        yield return ("chat-server.only-errors-cancel", FixtureKind.ChatServer, "--only errors,cancel");
        yield return ("chat-server.pipe", FixtureKind.ChatServer, "--pipe 4.4");
        yield return ("chat-server.gaps", FixtureKind.ChatServer, "--only gaps");
        yield return ("chat-server.grep", FixtureKind.ChatServer, "--grep HELLO");
        yield return ("chat-server.until", FixtureKind.ChatServer, "--until method=SendAttachment --context 3");
        yield return ("chat-client.dir-out-frames", FixtureKind.ChatClient, "--dir out --level frames --max-lines 40");
        yield return ("chat-client.sample-tokens", FixtureKind.ChatClient, "--sample 1/4 --max-tokens 300");
        yield return ("chat-client.max-lines", FixtureKind.ChatClient, "--max-lines 10 --collapse off");
        yield return ("server-all.slow-ndjson", FixtureKind.ServerAll, "--format ndjson --slow 5ms");
    }

    [Test]
    [MethodDataSource(nameof(GoldenCases))]
    public async Task Filters_and_budgets_match_the_golden(string name, FixtureKind kind, string options)
    {
        var (code, actual, error) = await RunAsync(["tail", Fixture(kind), .. options.Split(' ')]);
        await Assert.That(code).IsEqualTo((int)ExitCode.Ok).Because(error);
        var path = Path.Combine(GoldenDirectory(), name + ".txt");
        if (TextGolden.Updating)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, actual);
        }

        await Assert.That(File.Exists(path)).IsTrue().Because($"golden {path} is missing; run with {TextGolden.UpdateVariable}=1");
        await Assert.That(actual).IsEqualTo(TextGolden.Normalize(await File.ReadAllTextAsync(path)));
    }

    [Test]
    public async Task Without_options_tail_prints_the_renderer_output_of_every_session()
    {
        foreach (var kind in Sample.Fixtures.All)
        {
            var (code, actual, _) = await RunAsync(["tail", Fixture(kind)]);
            await Assert.That(code).IsEqualTo((int)ExitCode.Ok);
            var (recording, sessions) = TextGolden.Open(kind);
            using (recording)
            {
                var expected = new StringBuilder();
                foreach (var session in sessions)
                    expected.Append(TextRenderer.RenderToString(session));
                await Assert.That(actual).IsEqualTo(expected.ToString()).Because(kind.ToString());
            }
        }
    }

    [Test]
    [Arguments(FixtureKind.ChatServer, "messages", "")]
    [Arguments(FixtureKind.ServerAll, "messages", "")]
    [Arguments(FixtureKind.ServerAll, "frames", "--dir in")]
    [Arguments(FixtureKind.ChatClient, "messages", "--method Send*,Join --pipe 4")]
    public async Task Paging_with_after_has_no_duplicates_and_no_loss(FixtureKind kind, string level, string filter)
    {
        string[] filters = filter.Length == 0 ? [] : filter.Split(' ');
        var (_, whole, _) = await RunAsync(["tail", Fixture(kind), "--level", level, .. filters]);
        await Assert.That(whole.Length).IsGreaterThan(0);

        var pages = new StringBuilder();
        string? after = null;
        for (var page = 0; page < 1000; page++)
        {
            string[] resume = after is null ? [] : ["--after", after];
            var (code, text, error) = await RunAsync(["tail", Fixture(kind), "--level", level, "--max-lines", "7", .. filters, .. resume]);
            await Assert.That(code).IsEqualTo((int)ExitCode.Ok).Because(error);
            var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            after = null;
            foreach (var line in lines)
            {
                const string marker = "budget hit, ";
                if (line.StartsWith("… " + marker, StringComparison.Ordinal))
                    after = line[(line.IndexOf("--after ", StringComparison.Ordinal) + 8)..];
                else
                    pages.Append(line).Append('\n');
            }

            await Assert.That(lines.Length).IsLessThanOrEqualTo(8);
            if (after is null)
                break;
        }

        await Assert.That(pages.ToString()).IsEqualTo(whole);
    }

    [Test]
    public async Task Until_prints_the_context_and_stops_or_exits_4_when_never_met()
    {
        var (code, output, _) = await RunAsync(["tail", Fixture(FixtureKind.ChatServer), "--until", "state=canceled", "--context", "2"]);
        await Assert.That(code).IsEqualTo((int)ExitCode.Ok);
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        // The session header, two lines of context, the matching line, the stop line.
        await Assert.That(lines.Length).IsEqualTo(5);
        await Assert.That(lines[0]).StartsWith("@0x");
        await Assert.That(lines[3]).Contains("-> canceled");
        await Assert.That(lines[4]).StartsWith("… stopped: until state=canceled matched, 4 lines, --after ");

        // Resuming after the match continues right behind it.
        var cursor = lines[4][(lines[4].IndexOf("--after ", StringComparison.Ordinal) + 8)..];
        var (_, rest, _) = await RunAsync(["tail", Fixture(FixtureKind.ChatServer), "--after", cursor]);
        var (_, whole, _) = await RunAsync(["tail", Fixture(FixtureKind.ChatServer)]);
        await Assert.That(whole.EndsWith(rest, StringComparison.Ordinal)).IsTrue();
        await Assert.That(whole[..^rest.Length].TrimEnd('\n').EndsWith(lines[3], StringComparison.Ordinal)).IsTrue();

        // Alternatives: the first one to hold wins; a condition that never holds exits 4.
        var gap = await RunAsync(["tail", Fixture(FixtureKind.ChatServer), "--until", "state=exception|type=gap", "--context", "0"]);
        await Assert.That(gap.Code).IsEqualTo((int)ExitCode.Ok);
        await Assert.That(gap.Output).Contains("GAP out 1 ops");
        var never = await RunAsync(["tail", Fixture(FixtureKind.ChatServer), "--until", "method=Nope*"]);
        await Assert.That(never.Code).IsEqualTo((int)ExitCode.NotFound);
        await Assert.That(never.Output).IsEqualTo("… stopped: until method=Nope* not met, 0 lines\n");
    }

    [Test]
    public async Task Command_line_errors_are_usage_errors()
    {
        string[][] bad =
        [
            ["tail"],
            ["tail", "x.nntl", "--until", "colour=red"],
            ["tail", "x.nntl", "--until", "state=lost"],
            ["tail", "x.nntl", "--context", "3"],
            ["tail", "x.nntl", "--sample", "1/0"],
            ["tail", "x.nntl", "--level", "loud"],
            ["tail", "x.nntl", "--pipe", "a.b"],
            ["tail", "x.nntl", "--only", "warnings"],
            ["tail", "x.nntl", "--after", "not-a-cursor"],
            ["tail", "x.nntl", "--token", "t"],
            ["tail", "tcp://127.0.0.1:1", "--token", "t"],
            ["tail", "tcp://127.0.0.1:1", "--token", "t", "--session", "first", "--after", "AQEB_Ag_HAIAAAAWAAAAAAAAAAAAAACGru5rwuYapw"],
        ];
        foreach (var args in bad)
        {
            var (code, _, error) = await RunAsync(args);
            await Assert.That(code).IsEqualTo((int)ExitCode.Usage).Because(string.Join(' ', args));
            await Assert.That(error).StartsWith("nexnet-tap: ");
        }

        // A cursor made with other filters, or for a session the file does not hold.
        var (_, paged, _) = await RunAsync(["tail", Fixture(FixtureKind.ChatServer), "--max-lines", "3"]);
        var cursor = paged[(paged.IndexOf("--after ", StringComparison.Ordinal) + 8)..].Trim();
        var other = await RunAsync(["tail", Fixture(FixtureKind.ChatServer), "--after", cursor, "--dir", "in"]);
        await Assert.That(other.Code).IsEqualTo((int)ExitCode.Usage);
        await Assert.That(other.Error).Contains("different filters");
        var elsewhere = await RunAsync(["tail", Fixture(FixtureKind.ChatClient), "--after", cursor]);
        await Assert.That(elsewhere.Code).IsEqualTo((int)ExitCode.Usage);
        await Assert.That(elsewhere.Error).Contains("does not read");

        var parsed = CliArguments.TryParse(["tail", "uds:/tmp/x.sock", "--token", "t", "--session", "all", "--method", "A*,B", "--pipe", "≋4·4,7"],
            null, out var args2, out var problem);
        await Assert.That(parsed).IsTrue().Because(problem ?? "");
        await Assert.That(args2!.IsLiveTail).IsTrue();
        await Assert.That(args2.Methods).IsEquivalentTo(new[] { "A*", "B" });
        await Assert.That(args2.Pipes).IsEquivalentTo(new[] { new TailPipeId(4, 4), new TailPipeId(7, TailPipeId.Either) });
    }

    [Test]
    public async Task Follow_reads_a_recording_as_it_grows_until_its_footer()
    {
        var bytes = await File.ReadAllBytesAsync(Fixture(FixtureKind.ChatServer));
        var path = Path.Combine(Path.GetTempPath(), $"nntap-tail-{Guid.NewGuid():N}.nntl");
        try
        {
            await File.WriteAllBytesAsync(path, bytes.AsSpan(0, bytes.Length / 3).ToArray());
            var run = Task.Run(() => RunAsync(["tail", path, "--follow"]));
            await Task.Delay(400);
            await Assert.That(run.IsCompleted).IsFalse();
            await using (var file = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                await file.WriteAsync(bytes.AsMemory(bytes.Length / 3));

            var (code, output, error) = await run.WaitAsync(Wait);
            await Assert.That(code).IsEqualTo((int)ExitCode.Ok).Because(error);
            var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            await Assert.That(lines.Count(l => l.StartsWith("@0x", StringComparison.Ordinal))).IsEqualTo(1);
            await Assert.That(lines[^1]).IsEqualTo("+282.6 END Graceful from client");

            // Every call of the replay appears once (a call cut by the first third shows its outcome on a res line).
            var (_, replay, _) = await RunAsync(["tail", path]);
            foreach (var call in replay.Split('\n').Where(l => l.Contains("SendMessage(", StringComparison.Ordinal)))
            {
                var id = call.Split(' ')[1];
                await Assert.That(lines.Count(l => l.Contains(id + " SendMessage(", StringComparison.Ordinal))).IsEqualTo(1);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task Ctrl_c_on_a_followed_file_ends_with_the_resume_cursor()
    {
        var bytes = await File.ReadAllBytesAsync(Fixture(FixtureKind.ChatServer));
        var path = Path.Combine(Path.GetTempPath(), $"nntap-tail-{Guid.NewGuid():N}.nntl");
        try
        {
            await File.WriteAllBytesAsync(path, bytes.AsSpan(0, bytes.Length / 2).ToArray());
            using var interrupt = new CancellationTokenSource();
            var run = Task.Run(() => RunAsync(["tail", path, "--follow"], interrupt.Token));
            await Task.Delay(600);
            await interrupt.CancelAsync();
            var (code, output, _) = await run.WaitAsync(Wait);
            await Assert.That(code).IsEqualTo((int)ExitCode.Ok);
            var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            await Assert.That(lines[0]).StartsWith("@0x");
            await Assert.That(lines[^1]).StartsWith("… stopped: interrupted, ");
            await Assert.That(lines[^1]).Contains(" --after ");
            var cursor = lines[^1][(lines[^1].IndexOf("--after ", StringComparison.Ordinal) + 8)..];
            await Assert.That(TextCursor.TryParse(cursor, out var parsed)).IsTrue();
            await Assert.That(parsed.Phase).IsEqualTo((byte)1);
            await Assert.That(parsed.Frame).IsGreaterThan(0);

            // The cursor resumes the complete recording behind what was printed.
            await File.WriteAllBytesAsync(path, bytes);
            var (resumed, rest, error) = await RunAsync(["tail", path, "--after", cursor]);
            await Assert.That(resumed).IsEqualTo((int)ExitCode.Ok).Because(error);
            await Assert.That(rest).DoesNotContain("@0x");
            await Assert.That(rest.TrimEnd('\n').Split('\n')[^1]).IsEqualTo("+282.6 END Graceful from client");
        }
        finally
        {
            File.Delete(path);
        }
    }
}
