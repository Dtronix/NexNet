using System.Text;
using System.Text.Json;
using NexNet.Inspector.Core.Format;
using NexNet.Inspector.Core.Model;
using NexNet.Inspector.Core.Session;
using NexNet.Inspector.Core.Text;
using NexNet.Inspector.Sample;
using NexNet.Inspector.Tests.Session;

namespace NexNet.Inspector.Tests.Text;

/// <summary>The text renderer: determinism, budgets and cursors, filters, ndjson, values, live (incremental) use.</summary>
public class TextRendererTests
{
    private static CaptureSession Open(FixtureKind kind, int index = 0)
    {
        var recording = RecordingFile.Open(SessionFixtures.Path(kind));
        var session = recording.CreateSession(recording.Sessions[index].SessionId);
        recording.Index(session);
        return session;
    }

    private static string[] Lines(string text) => text.Split('\n', StringSplitOptions.RemoveEmptyEntries);

    [Test]
    public async Task Rendering_is_deterministic_across_renders_and_opens()
    {
        foreach (var level in new[] { TextLevel.Summary, TextLevel.Messages, TextLevel.Frames })
        {
            var options = new TextOptions { Level = level };
            var first = TextRenderer.RenderToString(Open(FixtureKind.ChatServer), options);
            var session = Open(FixtureKind.ChatServer);
            var second = TextRenderer.RenderToString(session, options);
            var third = TextRenderer.RenderToString(session, options);
            await Assert.That(second).IsEqualTo(first);
            await Assert.That(third).IsEqualTo(first);
        }
    }

    [Test]
    [Arguments(TextLevel.Messages, 7)]
    [Arguments(TextLevel.Frames, 50)]
    [Arguments(TextLevel.Bytes, 33)]
    [Arguments(TextLevel.Summary, 4)]
    public async Task Pages_resumed_by_cursor_add_up_to_the_whole_rendering(TextLevel level, int maxLines)
    {
        var session = Open(FixtureKind.ChatServer);
        var whole = TextRenderer.RenderToString(session, new TextOptions { Level = level });

        // Each page is a new renderer resumed from the previous page's printed cursor, as a CLI would.
        var pages = new StringBuilder();
        string? after = null;
        var count = 0;
        while (true)
        {
            TextCursor? cursor = null;
            if (after is not null)
            {
                await Assert.That(TextCursor.TryParse(after, out var parsed)).IsTrue();
                cursor = parsed;
            }

            using var output = new StringWriter();
            var result = new TextRenderer(session, new TextOptions { Level = level, MaxLines = maxLines, After = cursor }).Render(output);
            var lines = Lines(output.ToString());
            if (result.BudgetHit)
            {
                await Assert.That(lines.Length).IsEqualTo(maxLines + 1);
                await Assert.That(lines[^1]).StartsWith("… budget hit, " + result.Withheld + " withheld, --after ");
                after = lines[^1][(lines[^1].LastIndexOf(' ') + 1)..];
                await Assert.That(after).IsEqualTo(result.Next.Encode());
                foreach (var line in lines[..^1])
                    pages.Append(line).Append('\n');
            }
            else
            {
                await Assert.That(result.Complete).IsTrue();
                foreach (var line in lines)
                    pages.Append(line).Append('\n');
                break;
            }

            await Assert.That(++count).IsLessThan(1000);
        }

        await Assert.That(pages.ToString()).IsEqualTo(whole);
    }

    [Test]
    public async Task One_renderer_continues_after_its_budget_on_the_next_call()
    {
        var session = Open(FixtureKind.ChatClient);
        var whole = TextRenderer.RenderToString(session);
        var renderer = new TextRenderer(session, new TextOptions { MaxLines = 10 });
        var pages = new StringBuilder();
        TextRenderResult result;
        do
        {
            using var output = new StringWriter();
            result = renderer.Render(output);
            var lines = Lines(output.ToString());
            foreach (var line in result.BudgetHit ? lines[..^1] : lines)
                pages.Append(line).Append('\n');
        }
        while (result.BudgetHit);

        await Assert.That(pages.ToString()).IsEqualTo(whole);
        await Assert.That(renderer.IsComplete).IsTrue();

        // A completed renderer writes nothing more.
        using var after = new StringWriter();
        await Assert.That(renderer.Render(after).Lines).IsEqualTo(0);
    }

    [Test]
    public async Task A_token_budget_holds_and_counts_tokens_pessimistically()
    {
        var session = Open(FixtureKind.ChatServer);
        using var output = new StringWriter();
        var result = new TextRenderer(session, new TextOptions { MaxTokens = 300 }).Render(output);
        var lines = Lines(output.ToString());
        await Assert.That(result.BudgetHit).IsTrue();
        await Assert.That(result.Tokens).IsLessThanOrEqualTo(300);
        long counted = lines[..^1].Sum(line => TextBudget.Estimate(line));
        await Assert.That(counted).IsEqualTo(result.Tokens);
        await Assert.That(counted).IsGreaterThanOrEqualTo(lines[..^1].Sum(line => (line.Length + 1) / 4));

        await Assert.That(TextBudget.Estimate("abc")).IsEqualTo(1);
        await Assert.That(TextBudget.Estimate("abcdefg")).IsEqualTo(2);
        await Assert.That(TextBudget.Estimate("≋")).IsEqualTo(1);
        await Assert.That(TextBudget.Estimate("≋≋")).IsEqualTo(2);
    }

    [Test]
    public async Task Cursors_round_trip_and_reject_garbage_or_another_rendering()
    {
        var cursor = new TextCursor(0x21C3F08FC, TextLevel.Frames, 1, 812, 3, 2, 0xDEADBEEFCAFEF00D);
        await Assert.That(TextCursor.TryParse(cursor.Encode(), out var parsed)).IsTrue();
        await Assert.That(parsed).IsEqualTo(cursor);
        await Assert.That(TextCursor.TryParse("not a cursor", out _)).IsFalse();
        await Assert.That(TextCursor.TryParse("", out _)).IsFalse();
        await Assert.That(TextCursor.TryParse(cursor.Encode()[..20], out _)).IsFalse();

        var session = Open(FixtureKind.ChatServer);
        var start = TextCursor.Start(session.SessionId, TextLevel.Messages, TextFilter.All.Fingerprint());
        await Assert.That(() => new TextRenderer(session, new TextOptions { Level = TextLevel.Frames, After = start })).Throws<ArgumentException>();
        await Assert.That(() => new TextRenderer(session, new TextOptions { After = start with { SessionId = 7 } })).Throws<ArgumentException>();
        var inbound = new TextFilter { Directions = TextDirections.Inbound };
        await Assert.That(() => new TextRenderer(session, new TextOptions { Filter = inbound, After = start })).Throws<ArgumentException>();
        await Assert.That(inbound.Fingerprint()).IsNotEqualTo(TextFilter.All.Fingerprint());
        await Assert.That(new TextFilter { MatchKey = "--method Send*" }.Fingerprint()).IsNotEqualTo(new TextFilter { MatchKey = "--method Join" }.Fingerprint());
    }

    [Test]
    public async Task Filters_keep_a_direction_a_window_and_what_a_predicate_matches()
    {
        var session = Open(FixtureKind.ChatServer);
        var inbound = Lines(TextRenderer.RenderToString(session, new TextOptions { Filter = new TextFilter { Directions = TextDirections.Inbound } }));
        await Assert.That(inbound.Any(line => line.Contains(" >", StringComparison.Ordinal))).IsFalse();
        await Assert.That(inbound.Any(line => line.Contains(" <#", StringComparison.Ordinal))).IsTrue();

        var window = TextFilter.WindowFromMilliseconds(session, 20, 60);
        var records = new List<TextRecord>();
        new TextRenderer(session, new TextOptions { Filter = new TextFilter { Window = window } }).Collect(records);
        await Assert.That(records.Count).IsGreaterThan(3);
        foreach (var record in records.Where(r => r.Kind != TextRecordKind.Header))
            await Assert.That(record.TimeMs).IsBetween(20.0, 60.0);

        var calls = new TextFilter { Match = static r => r.Kind == TextRecordKind.Call && r.Name == "SendMessage", MatchKey = "SendMessage" };
        var only = Lines(TextRenderer.RenderToString(session, new TextOptions { Filter = calls, Header = false }));
        await Assert.That(only.Length).IsEqualTo(4);
        await Assert.That(only.All(line => line.Contains("SendMessage(", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task Ndjson_lines_are_json_objects_with_short_keys()
    {
        var session = Open(FixtureKind.ChatServer);
        var lines = Lines(TextRenderer.RenderToString(session, new TextOptions { Format = TextFormat.Ndjson, Level = TextLevel.Frames, MaxLines = 40 }));
        await Assert.That(lines.Length).IsEqualTo(41);
        var kinds = new HashSet<string>();
        foreach (var line in lines)
        {
            using var document = JsonDocument.Parse(line);
            kinds.Add(document.RootElement.GetProperty("k").GetString()!);
        }

        await Assert.That(kinds).Contains("hdr");
        await Assert.That(kinds).Contains("frame");
        await Assert.That(kinds).Contains("budget");
        using var budget = JsonDocument.Parse(lines[^1]);
        await Assert.That(TextCursor.TryParse(budget.RootElement.GetProperty("after").GetString(), out _)).IsTrue();

        // Glyphs stay literal (fewer tokens than \u escapes).
        var messages = TextRenderer.RenderToString(session, new TextOptions { Format = TextFormat.Ndjson });
        await Assert.That(messages).Contains("≋4·4");
    }

    [Test]
    public async Task Values_are_cut_to_the_width_and_depth()
    {
        var session = Open(FixtureKind.ChatServer);
        var narrow = TextRenderer.RenderToString(session, new TextOptions { ValueWidth = 8 });
        await Assert.That(narrow).Contains("\"hello e…\"");
        await Assert.That(narrow).DoesNotContain("hello everyone");

        var shallow = TextRenderer.RenderToString(session, new TextOptions { Depth = 0 });
        await Assert.That(shallow).Contains("SendMessage({…})");

        var wide = TextRenderer.RenderToString(session);
        await Assert.That(wide).Contains("SendMessage({\"general\" \"ada\" \"hello everyone\"");
    }

    [Test]
    public async Task Collapsing_folds_noise_and_runs_and_can_be_turned_off()
    {
        var session = Open(FixtureKind.ChatServer);
        var collapsed = TextRenderer.RenderToString(session);
        var expanded = TextRenderer.RenderToString(session, new TextOptions { Collapse = false });
        await Assert.That(collapsed).Contains("LogLine ×12 ");
        await Assert.That(collapsed).Contains("(…11 more)");
        await Assert.That(collapsed).Contains("ping ×");
        await Assert.That(expanded).DoesNotContain("×12");
        await Assert.That(Lines(expanded).Length).IsGreaterThan(Lines(collapsed).Length + 400);
    }

    [Test]
    public async Task Redacted_tokens_show_only_their_marker()
    {
        var session = Open(FixtureKind.ChatServer);
        var text = TextRenderer.RenderToString(session, new TextOptions { Level = TextLevel.Summary });
        await Assert.That(text).Contains("token=redacted(sha256:");
        var bytes = TextRenderer.RenderToString(session, new TextOptions { Level = TextLevel.Bytes, MaxLines = 12 });
        await Assert.That(bytes).Contains("ClientGreeting token redacted");
    }

    [Test]
    public async Task A_live_capture_renders_incrementally_with_every_call_once()
    {
        var path = SessionFixtures.Path(FixtureKind.ChatServer);
        var whole = TextRenderer.RenderToString(Open(FixtureKind.ChatServer));
        var calls = Lines(whole).Where(line => line.Contains(" <#", StringComparison.Ordinal) || line.Contains(" >#", StringComparison.Ordinal))
            .Select(line => line.Split(' ')[1]).ToArray();

        using var reader = NntlReader.Open(File.OpenRead(path));
        var live = new LiveCapture();
        TextRenderer? renderer = null;
        var output = new StringWriter();
        var block = 0;
        foreach (var entry in reader.Index.ToFileOrder())
        {
            live.Accept(reader.ReadBlock(entry.Offset));
            if (live.Session is null || ++block % 5 != 0)
                continue;
            renderer ??= new TextRenderer(live.Session);
            renderer.Render(output);
        }

        renderer!.Render(output);
        await Assert.That(renderer.IsComplete).IsTrue();
        var lines = Lines(output.ToString());
        await Assert.That(lines[0]).StartsWith("@0x");
        await Assert.That(lines[^1]).StartsWith("+");
        await Assert.That(lines[^1]).Contains("END Graceful");

        // Every call appears once as a call line; calls shown pending get their outcome on a result line.
        foreach (var id in calls)
        {
            var arrow = id[0];
            var callLines = lines.Where(line => line.Contains(" " + id + " ", StringComparison.Ordinal) && line.Contains('(')).ToArray();
            await Assert.That(callLines.Length).IsEqualTo(1).Because(id);
            if (callLines[0].Contains("-> …", StringComparison.Ordinal))
                await Assert.That(lines.Any(line => line.Contains(id[1..] + " ", StringComparison.Ordinal) && line.Contains(" -> ", StringComparison.Ordinal) && !line.Contains('('))).IsTrue().Because(id);
        }
    }

    [Test]
    public async Task A_session_attached_mid_stream_says_so()
    {
        var session = Open(FixtureKind.ServerAll, 0);
        await Assert.That(session.IsFromInception(TapDirection.Outbound) || session.IsFromInception(TapDirection.Inbound)).IsFalse();
        var lines = Lines(TextRenderer.RenderToString(session));
        await Assert.That(lines[1]).IsEqualTo("hs none (attached mid-stream)");
    }

    [Test]
    public async Task The_format_legend_fits_in_twenty_lines()
    {
        var lines = TextLegend.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        await Assert.That(lines.Length).IsLessThanOrEqualTo(20);
        await Assert.That(lines.All(line => line.Length <= 120)).IsTrue();
        await Assert.That(TextLegend.Text).EndsWith("\n");
    }
}
