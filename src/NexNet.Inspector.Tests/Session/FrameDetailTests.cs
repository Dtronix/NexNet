using NexNet.Inspector.Core.Assembly;
using NexNet.Inspector.Core.Format;
using NexNet.Inspector.Core.Framing;
using NexNet.Inspector.Core.Model;
using NexNet.Inspector.Core.Schema;
using NexNet.Inspector.Core.Session;
using NexNet.Inspector.Sample;
using NexNet.Inspector.Tests.Assembly;
using NexNet.Messages;
using NexNet.Serialization;

namespace NexNet.Inspector.Tests.Session;

/// <summary>
/// A capture session marks frames whose bytes were redacted (R), and the assembler summarizes a frame's body in a few
/// numbers for one-line labels (preamble version, greeting token and assigned ID, credits, pipe state, result, large
/// message kind and size).
/// </summary>
public class FrameDetailTests
{
    private static readonly byte[] Token = new byte[12];

    private static CaptureSession NewSession() => new(CaptureSource.Live, NntlFileHeader.Create("tests", "1"),
        new NntlSessionOpen(1, NntlSessionRole.Server, 1000, "Tcp", null, null, null, ReadOnlyMemory<byte>.Empty),
        new LiveOpStore(), new LiveOpStore(), SchemaResolution.Resolve(false, null, null, SchemaChoice.Embedded));

    // One op per frame, in script order; the ranges given (frame index, start, length) are recorded as redacted.
    private static CaptureSession Feed(WireScript script, params (int Frame, int Start, int Length)[] redactions)
    {
        var session = NewSession();
        long op = 0;
        for (var i = 0; i < script.Frames.Count; i++)
        {
            var (direction, bytes) = script.Frames[i];
            var ranges = redactions.Where(r => r.Frame == i).Select(r => new NntlByteRange(r.Start, r.Length)).ToArray();
            lock (session.Gate)
            {
                session.AddOp(new NntlTransportOp(1, direction, op, 1000 + op, ranges.Length > 0 ? NntlTransportOpFlags.Redacted : NntlTransportOpFlags.None,
                    ranges, bytes), 0);
            }

            op++;
        }

        return session;
    }

    private static void ClientGreetingBody(ref MsgPackWriter w)
    {
        w.WriteArrayHeader(6);
        w.Write("v2");
        w.Write(11);
        w.Write(22);
        w.WriteBinary(Token);
        w.Write(65536);
        w.Write(32);
    }

    private static void ServerGreetingBody(ref MsgPackWriter w)
    {
        w.WriteArrayHeader(4);
        w.Write(22);
        w.Write(0x12_3A21L);
        w.Write(65536);
        w.Write(32);
    }

    private static FrameRecord Frame(CaptureSession session, TapDirection direction, long seq)
    {
        lock (session.Gate)
            return session.TryGetFrame(new FrameKey(direction, seq), out var frame) ? frame : throw new InvalidOperationException("no frame");
    }

    private static FrameDetail Detail(CaptureSession session, TapDirection direction, long seq)
    {
        lock (session.Gate)
            return session.Assembler.GetDetail(direction, seq);
    }

    [Test]
    public async Task Only_frames_whose_bytes_overlap_a_redacted_range_carry_R()
    {
        // Inbound: preamble, ClientGreeting with its token redacted, a ping. The token starts after the 3-byte header,
        // the array marker, "v2" (3 bytes), two ints (1 byte each) and the bin header (2 bytes).
        var script = new WireScript();
        script.Frames.Add((TapDirection.Inbound, [0x4E, 0x6E, 0x50, 0x14, 0, 0, 0, 1]));
        script.Message(TapDirection.Inbound, MessageType.ClientGreeting, ClientGreetingBody);
        script.Ping(TapDirection.Inbound);
        var session = Feed(script, (1, 3 + 1 + 3 + 2 + 2, Token.Length));

        await Assert.That(Frame(session, TapDirection.Inbound, 0).Flags.HasFlag(FrameFlags.Redacted)).IsFalse();
        await Assert.That(Frame(session, TapDirection.Inbound, 1).Flags.HasFlag(FrameFlags.Redacted)).IsTrue();
        await Assert.That(Frame(session, TapDirection.Inbound, 2).Flags.HasFlag(FrameFlags.Redacted)).IsFalse();
    }

    [Test]
    public async Task A_redaction_across_two_frames_of_one_op_marks_both()
    {
        // The preamble and three one-byte pings in one read; the range covers the second and third ping.
        var script = new WireScript().Ping(TapDirection.Inbound).Ping(TapDirection.Inbound).Ping(TapDirection.Inbound);
        byte[] bytes = [0x4E, 0x6E, 0x50, 0x14, 0, 0, 0, 1, .. script.Frames.SelectMany(f => f.Bytes)];
        var session = NewSession();
        lock (session.Gate)
            session.AddOp(new NntlTransportOp(1, TapDirection.Inbound, 0, 1000, NntlTransportOpFlags.Redacted, new[] { new NntlByteRange(9, 2) }, bytes), 0);

        await Assert.That(Frame(session, TapDirection.Inbound, 0).Flags.HasFlag(FrameFlags.Redacted)).IsFalse();
        await Assert.That(Frame(session, TapDirection.Inbound, 1).Flags.HasFlag(FrameFlags.Redacted)).IsFalse();
        await Assert.That(Frame(session, TapDirection.Inbound, 2).Flags.HasFlag(FrameFlags.Redacted)).IsTrue();
        await Assert.That(Frame(session, TapDirection.Inbound, 3).Flags.HasFlag(FrameFlags.Redacted)).IsTrue();
    }

    [Test]
    public async Task Details_summarize_greetings_credits_pipe_states_results_and_large_messages()
    {
        var script = new WireScript().Preamble();
        script.Message(TapDirection.Inbound, MessageType.ClientGreeting, ClientGreetingBody)
            .Message(TapDirection.Outbound, MessageType.ServerGreeting, ServerGreetingBody)
            .Message(TapDirection.Outbound, MessageType.InvocationCredit, static (ref MsgPackWriter w) =>
            {
                w.WriteArrayHeader(1);
                w.Write((ushort)3);
            })
            .PipeState(TapDirection.Outbound, 0x0101, PipeStateFlags.Ready, 4096)
            .Invocation(TapDirection.Inbound, 5, 2, InvocationFlags.None, static (ref MsgPackWriter w) => w.WriteArrayHeader(0))
            .Result(TapDirection.Outbound, 5, InvocationResultState.Canceled)
            .MessageStart(TapDirection.Inbound, 6, LargeMessageKind.Argument, 49_152);
        var session = Feed(script);

        await Assert.That(Detail(session, TapDirection.Outbound, 0)).IsEqualTo(new FrameDetail(FrameDetailKind.Preamble, 1));
        await Assert.That(Detail(session, TapDirection.Inbound, 0)).IsEqualTo(new FrameDetail(FrameDetailKind.AuthToken, Token.Length));
        await Assert.That(Detail(session, TapDirection.Outbound, 1)).IsEqualTo(new FrameDetail(FrameDetailKind.ClientId, 0x12_3A21L));
        await Assert.That(Detail(session, TapDirection.Outbound, 2)).IsEqualTo(new FrameDetail(FrameDetailKind.Credit, 3));
        await Assert.That(Detail(session, TapDirection.Outbound, 3)).IsEqualTo(new FrameDetail(FrameDetailKind.PipeState, (long)PipeStateFlags.Ready));
        await Assert.That(Detail(session, TapDirection.Inbound, 1).Kind).IsEqualTo(FrameDetailKind.None);
        await Assert.That(Detail(session, TapDirection.Outbound, 4)).IsEqualTo(new FrameDetail(FrameDetailKind.Result, (long)InvocationResultState.Canceled));
        await Assert.That(Detail(session, TapDirection.Inbound, 2))
            .IsEqualTo(new FrameDetail(FrameDetailKind.MessageStart, (long)LargeMessageKind.Argument, 49_152));
        await Assert.That(Detail(session, TapDirection.Inbound, 99)).IsEqualTo(FrameDetail.None);
    }

    [Test]
    public async Task The_fixture_ClientGreeting_is_redacted_and_the_preamble_reads_version_1()
    {
        using var recording = RecordingFile.Open(SessionFixtures.Path(FixtureKind.ChatServer));
        var session = recording.CreateSession(recording.Sessions[0].SessionId);
        await recording.IndexAsync(session, null);

        FrameRecord frame;
        int redacted = 0;
        FrameDetail preamble, token;
        lock (session.Gate)
        {
            var greeting = session.Assembler.Greetings.ToArray().First(g => g.Kind == GreetingKind.Client);
            session.TryGetFrame(new FrameKey(greeting.Direction, greeting.Seq), out frame);
            foreach (var key in session.FrameOrder)
            {
                session.TryGetFrame(key, out var any);
                if ((any.Flags & FrameFlags.Redacted) != 0) redacted++;
            }

            preamble = session.Assembler.GetDetail(greeting.Direction, 0);
            token = session.Assembler.GetDetail(greeting.Direction, greeting.Seq);
        }

        await Assert.That(frame.Flags.HasFlag(FrameFlags.Redacted)).IsTrue();
        await Assert.That(redacted).IsEqualTo(1).Because("only the ClientGreeting carries a token");
        await Assert.That(preamble).IsEqualTo(new FrameDetail(FrameDetailKind.Preamble, 1));
        await Assert.That(token.Kind).IsEqualTo(FrameDetailKind.AuthToken);
        await Assert.That(token.Value).IsGreaterThan(0);
    }
}
