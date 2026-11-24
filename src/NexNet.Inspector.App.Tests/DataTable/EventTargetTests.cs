using NexNet.Inspector.App.Controls.DataTable.Events;
using NexNet.Inspector.App.Controls.Ladder;
using NexNet.Inspector.App.Model;
using NexNet.Inspector.App.Tests.Documents;
using NexNet.Inspector.Core.Assembly;
using NexNet.Inspector.Core.Framing;
using NexNet.Inspector.Core.Session;
using NexNet.Messages;

namespace NexNet.Inspector.App.Tests.DataTable;

/// <summary>
/// The Events Target column and the ladder's label say what a frame's body adds (screens/inspect-session-start):
/// "NnP␔ · v1", "auth token · redacted", "clientId 0x3A21", "+1", "pipe 1·1 Users · Ready", "inv 1 · Completed".
/// </summary>
public class EventTargetTests
{
    [Test]
    public async Task Details_read_as_the_package_writes_them()
    {
        EventRow row = new() { Kind = FrameKind.Frame, Type = MessageType.Ping };
        await Assert.That(EventText.Target(row with { Kind = FrameKind.Preamble, Detail = new FrameDetail(FrameDetailKind.Preamble, 1) }))
            .IsEqualTo(FrameDetailText.PreambleMagic + " · v1");
        await Assert.That(FrameDetailText.PreambleMagic).IsEqualTo("NnP" + (char)0x2414);
        await Assert.That(EventText.Target(row with { Detail = new FrameDetail(FrameDetailKind.AuthToken, 96), Flags = FrameFlags.Redacted }))
            .IsEqualTo("auth token · redacted");
        await Assert.That(EventText.Target(row with { Detail = new FrameDetail(FrameDetailKind.AuthToken, 96) })).IsEqualTo("auth token · 96 B");
        await Assert.That(EventText.Target(row with { Detail = new FrameDetail(FrameDetailKind.AuthToken, -1) })).IsEqualTo("no auth token");
        await Assert.That(EventText.Target(row with { Detail = new FrameDetail(FrameDetailKind.ClientId, 0x12_3A21) })).IsEqualTo("clientId 0x3A21");
        await Assert.That(EventText.Target(row with { Detail = new FrameDetail(FrameDetailKind.Credit, 1) })).IsEqualTo("+1");
        await Assert.That(EventText.Target(row with
        {
            Target = EventTargetKind.Pipe, TargetId = 1 | (1 << 8), PipeLabel = "Users",
            Detail = new FrameDetail(FrameDetailKind.PipeState, (long)PipeStateFlags.Ready),
        })).IsEqualTo("pipe 1·1 Users · Ready");
        await Assert.That(EventText.Target(row with { Target = EventTargetKind.Channel, TargetId = 4 | (2 << 8), PipeLabel = "LogLine" }))
            .IsEqualTo("chan 4·2 LogLine");
        await Assert.That(EventText.Target(row with
        {
            Target = EventTargetKind.Invocation, TargetId = 1, MethodName = "Join",
            Detail = new FrameDetail(FrameDetailKind.Result, (long)InvocationResultState.CompletedResult),
        })).IsEqualTo("inv 1 · Completed");
        await Assert.That(EventText.Target(row with
        {
            Target = EventTargetKind.Invocation, TargetId = 5, MethodName = "SendAttachment",
            Detail = new FrameDetail(FrameDetailKind.MessageStart, (long)LargeMessageKind.Argument, 49_152),
        })).IsEqualTo("inv 5 · Arg · 49,152 B");
        await Assert.That(EventText.Target(row with { Target = EventTargetKind.Invocation, TargetId = 4, MethodName = "SendMessage" }))
            .IsEqualTo("inv 4 SendMessage");
        await Assert.That(FrameDetailText.PipeState(PipeStateFlags.Complete)).IsEqualTo("Complete");
        await Assert.That(FrameDetailText.PipeState(PipeStateFlags.Refused)).IsEqualTo("Refused");
    }

    [Test]
    public async Task The_fixture_handshake_rows_carry_their_details_and_the_R_chip()
    {
        using var recording = RecordingFile.Open(Wait.Fixture("chat-server.nntl"));
        CaptureSession session = recording.CreateSession(recording.Sessions[0].SessionId);
        await recording.IndexAsync(session);

        EventsTableSource events = new();
        events.Attach(session);
        string[] targets = new string[4];
        FrameFlags greetingFlags = FrameFlags.None;
        for (int i = 0; i < 4; i++)
        {
            await Assert.That(events.TryGetRow(i, out EventRow row)).IsTrue();
            targets[i] = EventText.Target(row);
            if (row.Type == MessageType.ClientGreeting) greetingFlags = row.Flags;
        }

        // Both preambles, then the greetings; the client's token is redacted by the tap.
        await Assert.That(targets.Count(t => t == FrameDetailText.PreambleMagic + " · v1")).IsEqualTo(2);
        await Assert.That(targets).Contains("auth token · redacted");
        await Assert.That(targets.Any(t => t.StartsWith("clientId 0x", StringComparison.Ordinal))).IsTrue();
        await Assert.That(greetingFlags.HasFlag(FrameFlags.Redacted)).IsTrue();

        // The ladder labels the same frames, and names a pipe state's pipe.
        LadderModel model = new();
        new LadderProjection(model).Update(session);
        List<string> details = [];
        for (int i = 0; i < model.Count; i++) details.Add(LadderText.Detail(model[i], model.GetLabel(model[i].Name)));
        await Assert.That(details).Contains("auth token · redacted");
        await Assert.That(details.Any(d => d.StartsWith("pipe ", StringComparison.Ordinal) && d.EndsWith(" · Ready", StringComparison.Ordinal))).IsTrue();
        // The ladder names a collection's pipe as the collection it carries (it gets the CO chip, not CH).
        await Assert.That(details.Any(d => d.StartsWith("coll 1·1 Users", StringComparison.Ordinal))).IsTrue();
    }
}
