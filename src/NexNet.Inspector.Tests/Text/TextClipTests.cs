using NexNet.Inspector.Core.Assembly;
using NexNet.Inspector.Core.Model;
using NexNet.Inspector.Core.Session;
using NexNet.Inspector.Core.Text;
using NexNet.Inspector.Sample;
using NexNet.Inspector.Tests.Session;

namespace NexNet.Inspector.Tests.Text;

/// <summary>Copy for LLM's text (Core <see cref="TextClip"/>): a range or an entity, header first, within its budget.</summary>
public class TextClipTests
{
    private static (RecordingFile Recording, CaptureSession Session) Open(FixtureKind kind)
    {
        var recording = RecordingFile.Open(SessionFixtures.Path(kind));
        var session = recording.CreateSession(recording.Sessions[0].SessionId);
        recording.Index(session);
        return (recording, session);
    }

    private static string[] Lines(string text) => text.Split('\n', StringSplitOptions.RemoveEmptyEntries);

    [Test]
    public async Task A_range_is_the_header_a_clip_line_and_the_lines_inside_it()
    {
        var (recording, session) = Open(FixtureKind.ChatClient);
        using (recording)
        {
            var all = new TimeWindow(session.StartTicks, session.EndTicks + 1);
            var text = TextClip.Range(session, all, "view");
            var lines = Lines(text);
            await Assert.That(lines[0]).StartsWith("@0x");
            await Assert.That(lines[1]).Matches("^clip view \\+0\\.0\\.\\.\\+[0-9.]+ \\([0-9.]+ms\\)$");
            await Assert.That(text).Contains("Join(");
            await Assert.That(lines[^1]).Contains(" END ");
            await Assert.That(lines.Sum(l => TextBudget.Estimate(l))).IsLessThanOrEqualTo(TextClip.DefaultMaxTokens);

            // A window that holds only the first frames leaves the later calls out.
            var early = TextFilter.WindowFromMilliseconds(session, 0, 0.001);
            var part = TextClip.Range(session, early, "scope");
            await Assert.That(Lines(part)[1]).StartsWith("clip scope +0.0..+0.0");
            await Assert.That(part).DoesNotContain("SendAttachment(");
        }
    }

    [Test]
    public async Task A_budget_cuts_the_clip_with_a_resumable_budget_line()
    {
        var (recording, session) = Open(FixtureKind.ChatServer);
        using (recording)
        {
            var text = TextClip.Range(session, new TimeWindow(session.StartTicks, session.EndTicks + 1), "view",
                new TextOptions { Level = TextLevel.Frames, MaxTokens = 200 });
            var lines = Lines(text);
            await Assert.That(lines[0]).StartsWith("@0x");
            await Assert.That(lines[^1]).Matches("^… budget hit, [0-9]+ withheld, --after \\S+$");
            await Assert.That(lines.SkipLast(1).Sum(l => TextBudget.Estimate(l))).IsLessThanOrEqualTo(200);
        }
    }

    [Test]
    public async Task An_entity_reads_as_show_or_trace_and_ids_name_invocations_pipes_and_frames()
    {
        var (recording, session) = Open(FixtureKind.ChatClient);
        using (recording)
        {
            int join;
            lock (session.Gate)
                join = session.Assembler.Invocations.ToArray().Select((row, i) => (row, i)).First(x => x.row.InvocationId >= 0).i;
            var id = TextClip.IdOf(session, EntityKind.Invocation, join);
            await Assert.That(id).StartsWith("#");
            await Assert.That(TextClip.IdOf(session, EntityKind.Pipe, 0)).IsNotNull();
            await Assert.That(TextClip.IdOf(session, EntityKind.Greeting, 0)).IsNull();
            await Assert.That(TextClip.IdOf(session, EntityKind.Invocation, 100_000)).IsNull();
            FrameKey first;
            lock (session.Gate)
                first = session.FrameOrder[0];
            await Assert.That(TextClip.IdOf(session, first)).IsEqualTo("f1");
            await Assert.That(TextClip.IdOf(session, new FrameKey(TapDirection.Inbound, 1_000_000))).IsNull();

            var show = Lines(TextClip.Entity(session, id!, TextClipDetail.Show)!);
            await Assert.That(show[0]).StartsWith("@0x");
            await Assert.That(show[1]).Contains(id!);
            var trace = TextClip.Entity(session, id!, TextClipDetail.Trace)!;
            await Assert.That(Lines(trace)[0]).StartsWith("@0x");
            await Assert.That(trace).Contains("frag");
            await Assert.That(TextClip.Entity(session, "#99999", TextClipDetail.Show)).IsNull();
        }
    }
}
