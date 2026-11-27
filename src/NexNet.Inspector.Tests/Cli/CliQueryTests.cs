using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using NexNet.Inspector.Cli;
using NexNet.Inspector.Core.Text;
using NexNet.Inspector.Sample;
using NexNet.Inspector.Tests.Session;
using NexNet.Inspector.Tests.Tap;
using NexNet.Inspector.Tests.Text;
using NexNet.Inspector.Tap;

namespace NexNet.Inspector.Tests.Cli;

/// <summary>
/// The drill-down queries (summary, show, trace, around, errors, slow, pipes, collections, schema, diff, export, format)
/// on the fixtures: golden outputs in Golden/Queries/ (rewrite with NEXNET_INSPECTOR_UPDATE_GOLDEN=1 and review the
/// diff), paging, the export round trip, errors, and a live session.
/// </summary>
public class CliQueryTests
{
    private static string Fixture(FixtureKind kind) => SessionFixtures.Path(kind);

    private static string GoldenPath(string name, [CallerFilePath] string source = "")
        => Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(source))!, "Golden", "Queries", name + ".txt");

    private static async Task<(int Code, string Output, string Error)> RunAsync(params string[] args)
    {
        var output = new StringWriter { NewLine = "\n" };
        var error = new StringWriter();
        var code = await new TapCli(output, error).RunAsync(args, null, CancellationToken.None);
        return (code, output.ToString(), error.ToString());
    }

    public static IEnumerable<Func<(string Name, FixtureKind Kind, string[] Args)>> Cases()
    {
        yield return () => ("chat-client.summary", FixtureKind.ChatClient, ["summary"]);
        yield return () => ("chat-server.summary", FixtureKind.ChatServer, ["summary"]);
        yield return () => ("server-all.summary", FixtureKind.ServerAll, ["summary"]);
        yield return () => ("legacy-schema.summary", FixtureKind.LegacySchema, ["summary"]);
        yield return () => ("server-all-truncated.summary", FixtureKind.Truncated, ["summary"]);
        yield return () => ("chat-client.show-4-bytes", FixtureKind.ChatClient, ["show", "4", "--bytes"]);
        yield return () => ("chat-client.show-2-ndjson", FixtureKind.ChatClient, ["show", "#2", "--format", "ndjson"]);
        yield return () => ("chat-client.show-p5", FixtureKind.ChatClient, ["show", "p5"]);
        yield return () => ("chat-client.show-f13-bytes", FixtureKind.ChatClient, ["show", "f13", "--bytes"]);
        yield return () => ("chat-client.trace-4", FixtureKind.ChatClient, ["trace", "4"]);
        yield return () => ("chat-client.around", FixtureKind.ChatClient, ["around", "+27", "±2"]);
        yield return () => ("chat-server.errors", FixtureKind.ChatServer, ["errors"]);
        yield return () => ("server-all-truncated.errors", FixtureKind.Truncated, ["errors"]);
        yield return () => ("chat-client.slow", FixtureKind.ChatClient, ["slow", "--over", "5ms"]);
        yield return () => ("chat-client.pipes", FixtureKind.ChatClient, ["pipes"]);
        yield return () => ("server-all.collections", FixtureKind.ServerAll, ["collections", "--contents"]);
        yield return () => ("chat-client.schema", FixtureKind.ChatClient, ["schema"]);
    }

    [Test]
    [MethodDataSource(nameof(Cases))]
    public async Task Query_output_matches_the_golden(string name, FixtureKind kind, string[] args)
    {
        string[] line = [args[0], Fixture(kind), .. args[1..]];
        var (code, output, error) = await RunAsync(line);
        await Assert.That(code).IsEqualTo((int)ExitCode.Ok).Because(error);
        await AssertGolden(name, output);
    }

    [Test]
    public async Task Diff_lists_what_the_variant_changed_worst_first()
    {
        var (code, output, error) = await RunAsync("diff", Fixture(FixtureKind.ChatClient), Fixture(FixtureKind.ChatClientVariant));
        await Assert.That(code).IsEqualTo((int)ExitCode.Ok).Because(error);
        await AssertGolden("chat-client.diff-variant", output);

        // The variant's extra call is a high difference, listed before any medium one; its first message's text differs.
        var lines = output.Split('\n');
        var firstHigh = Array.FindIndex(lines, l => l.StartsWith('+') && l.Contains(" high ", StringComparison.Ordinal));
        var firstMedium = Array.FindIndex(lines, l => l.StartsWith('+') && l.Contains(" medium ", StringComparison.Ordinal));
        await Assert.That(firstHigh).IsGreaterThan(0);
        await Assert.That(firstHigh).IsLessThan(firstMedium);
        await Assert.That(output).Contains("only B");
        await Assert.That(output).Contains("~ args.message.Text: \"hello everyone\" → \"hello everyone!\"");
    }

    [Test]
    public async Task A_budget_pages_a_query_without_loss_or_repeats()
    {
        var file = Fixture(FixtureKind.ServerAll);
        var (_, whole, _) = await RunAsync("pipes", file);
        var pages = new StringBuilder();
        string? after = null;
        for (var page = 0; page < 50; page++)
        {
            string[] args = after is null ? ["pipes", file, "--max-lines", "4"] : ["pipes", file, "--max-lines", "4", "--after", after];
            var (code, output, _) = await RunAsync(args);
            await Assert.That(code).IsEqualTo((int)ExitCode.Ok);
            var budget = output.IndexOf("… budget hit", StringComparison.Ordinal);
            if (budget < 0)
            {
                pages.Append(output);
                after = null;
                break;
            }

            pages.Append(output, 0, budget);
            var line = output[budget..].TrimEnd('\n');
            after = line[(line.LastIndexOf(' ') + 1)..];
        }

        await Assert.That(after).IsNull();
        await Assert.That(pages.ToString()).IsEqualTo(whole);

        // A cursor of another query is refused.
        var (_, first, _) = await RunAsync("pipes", file, "--max-lines", "2");
        var cursor = first.TrimEnd('\n')[(first.TrimEnd('\n').LastIndexOf(' ') + 1)..];
        var (refused, _, error) = await RunAsync("schema", file, "--after", cursor);
        await Assert.That(refused).IsEqualTo((int)ExitCode.Usage);
        await Assert.That(error).Contains("another query");
    }

    [Test]
    public async Task Export_writes_a_section_that_info_reads_back()
    {
        var path = Path.Combine(Path.GetTempPath(), $"nntap-export-{Guid.NewGuid():N}.nntl");
        try
        {
            var (code, output, error) = await RunAsync("export", Fixture(FixtureKind.ChatClient), "--from", "+20", "--to", "+60", "-o", path);
            await Assert.That(code).IsEqualTo((int)ExitCode.Ok).Because(error);
            await Assert.That(output).Contains("exported +20.0..+60.0:");
            await Assert.That(output).Contains("handshake");

            var (infoCode, json, _) = await RunAsync("info", path, "--json", "--messages");
            await Assert.That(infoCode).IsEqualTo((int)ExitCode.Ok);
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            await Assert.That(root.GetProperty("indexSource").GetString()).IsEqualTo("Footer");
            var sessions = root.GetProperty("sessions");
            await Assert.That(sessions.GetArrayLength()).IsEqualTo(1);
            var session = sessions[0];
            await Assert.That(session.GetProperty("outbound").GetProperty("frames").GetInt64()).IsGreaterThan(0);
            await Assert.That(session.GetProperty("schemaEmbedded").GetBoolean()).IsTrue();

            // The section reads on its own: the handshake and the attachment call inside it.
            var (_, summary, _) = await RunAsync("summary", path);
            await Assert.That(summary).Contains("hs ok v1");
            await Assert.That(summary).Contains("SendAttachment ×1 ok 1");

            // An existing file is kept without --force.
            var (again, _, refused) = await RunAsync("export", Fixture(FixtureKind.ChatClient), "-o", path);
            await Assert.That(again).IsEqualTo((int)ExitCode.Failed);
            await Assert.That(refused).Contains("--force");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task Help_format_prints_the_legend()
    {
        var (code, output, _) = await RunAsync("help", "format");
        await Assert.That(code).IsEqualTo((int)ExitCode.Ok);
        await Assert.That(output).IsEqualTo(TextLegend.Text);
        var (formatCode, format, _) = await RunAsync("format");
        await Assert.That(formatCode).IsEqualTo((int)ExitCode.Ok);
        await Assert.That(format).IsEqualTo(TextLegend.Text);
        await Assert.That(TextLegend.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length).IsLessThanOrEqualTo(20);
    }

    [Test]
    public async Task Ids_that_do_not_parse_or_resolve_fail_with_their_codes()
    {
        var file = Fixture(FixtureKind.ChatClient);
        var (notAnId, _, error) = await RunAsync("show", file, "zz");
        await Assert.That(notAnId).IsEqualTo((int)ExitCode.Usage);
        await Assert.That(error).Contains("not an ID");

        var (missing, _, missingError) = await RunAsync("show", file, "#999");
        await Assert.That(missing).IsEqualTo((int)ExitCode.NotFound);
        await Assert.That(missingError).Contains("no session holds #999");

        var (frame, _, _) = await RunAsync("trace", file, "f100000");
        await Assert.That(frame).IsEqualTo((int)ExitCode.NotFound);

        // Pipe IDs in every spelling name the same pipe.
        var (_, glyph, _) = await RunAsync("show", file, "≋4·4");
        var (_, ascii, _) = await RunAsync("show", file, "p4.4");
        await Assert.That(ascii).IsEqualTo(glyph);
        await Assert.That(glyph).Contains("LogLine by #0.6 TailLogs");
    }

    [Test]
    public async Task Command_lines_are_checked_before_anything_is_read()
    {
        var file = Fixture(FixtureKind.ChatClient);
        await Assert.That((await RunAsync("diff", file)).Code).IsEqualTo((int)ExitCode.Usage);
        await Assert.That((await RunAsync("show", file)).Code).IsEqualTo((int)ExitCode.Usage);
        await Assert.That((await RunAsync("summary")).Code).IsEqualTo((int)ExitCode.Usage);
        await Assert.That((await RunAsync("around", file, "soon")).Code).IsEqualTo((int)ExitCode.Usage);
        await Assert.That((await RunAsync("export", file)).Code).IsEqualTo((int)ExitCode.Usage);
        await Assert.That((await RunAsync("summary", file, "--format", "xml")).Code).IsEqualTo((int)ExitCode.Usage);
        await Assert.That((await RunAsync("summary", file, "--bytes")).Code).IsEqualTo((int)ExitCode.Usage);
        await Assert.That((await RunAsync("summary", "--endpoint", "tcp://127.0.0.1:1")).Code).IsEqualTo((int)ExitCode.Usage);
    }

    [Test]
    public async Task Session_picks_one_session_by_decimal_or_header_hex_id()
    {
        var file = Fixture(FixtureKind.ServerAll);
        var (_, hex, _) = await RunAsync("summary", file, "--session", "@0x42F6FD6BC");
        var (_, dec, _) = await RunAsync("summary", file, "--session", 0x42F6FD6BCL.ToString(System.Globalization.CultureInfo.InvariantCulture));
        await Assert.That(hex).IsEqualTo(dec);
        await Assert.That(hex).StartsWith("@0x42F6FD6BC ");
        await Assert.That(hex.Split("\n@0x").Length).IsEqualTo(1);

        var (missing, _, _) = await RunAsync("summary", file, "--session", "7");
        await Assert.That(missing).IsEqualTo((int)ExitCode.NotFound);
    }

    [Test]
    public async Task Times_read_as_the_lines_write_them()
    {
        await Assert.That(QueryCommand.TryParseTime("+27.5", out var a) && a == 27.5).IsTrue();
        await Assert.That(QueryCommand.TryParseTime("±10", out var b) && b == 10).IsTrue();
        await Assert.That(QueryCommand.TryParseTime("1.5s", out var c) && c == 1500).IsTrue();
        await Assert.That(QueryCommand.TryParseTime("0", out var d) && d == 0).IsTrue();
        await Assert.That(QueryCommand.TryParseTime("soon", out _)).IsFalse();
        await Assert.That(QueryCommand.TryParseTime("5h", out _)).IsFalse();
    }

    [Test]
    public async Task A_live_query_reads_what_arrived_during_the_duration()
    {
        await using var h = new TapHarness(TapTransport.Tcp);
        await h.StartAsync();
        await h.StartEndpointAsync(TapTransport.Tcp);
        var client = await h.ConnectClientAsync();
        await h.WaitForSessionAsync();
        var token = Encoding.UTF8.GetString(TapHarness.EndpointToken);

        var run = RunAsync("summary", "--endpoint", h.EndpointAddress!, "--token", token, "--session", "first", "--duration", "1500ms");
        // Traffic while the query is attached.
        for (var i = 0; i < 20 && !run.IsCompleted; i++)
        {
            await client.Proxy.Echo([(byte)i]);
            await Task.Delay(50);
        }

        var (code, output, error) = await run.WaitAsync(TimeSpan.FromSeconds(30));
        await Assert.That(code).IsEqualTo((int)ExitCode.Ok).Because(error);
        await Assert.That(output).StartsWith("@0x");
        await Assert.That(output).Contains("hs none (attached mid-stream)");
        await Assert.That(output).Contains("open now");

        // A live export is a recording: refused when the server does not allow recording.
        var path = Path.Combine(Path.GetTempPath(), $"nntap-export-{Guid.NewGuid():N}.nntl");
        var (refused, _, refusal) = await RunAsync("export", "--endpoint", h.EndpointAddress!, "--token", token, "-o", path, "--duration", "200ms");
        await Assert.That(refused).IsEqualTo((int)ExitCode.Refused);
        await Assert.That(refusal).Contains("Recording disabled by server config");
        await Assert.That(File.Exists(path)).IsFalse();
    }

    private static async Task AssertGolden(string name, string actual)
    {
        var path = GoldenPath(name);
        if (TextGolden.Updating)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, actual);
        }

        await Assert.That(File.Exists(path)).IsTrue().Because($"golden {path} is missing; run with {TextGolden.UpdateVariable}=1");
        var expected = TextGolden.Normalize(await File.ReadAllTextAsync(path));
        await Assert.That(actual).IsEqualTo(expected);
    }
}
