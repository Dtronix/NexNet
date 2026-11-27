using NexNet.Inspector.Core.Assembly;
using NexNet.Inspector.Core.Findings;
using NexNet.Inspector.Core.Format;
using NexNet.Inspector.Core.Model;
using NexNet.Inspector.Core.Schema;
using NexNet.Inspector.Core.Session;
using NexNet.Inspector.Core.Text;
using NexNet.Inspector.Sample;
using NexNet.Inspector.Tests.Assembly;
using NexNet.Inspector.Tests.Session;
using NexNet.Messages;
using NexNet.Serialization;

namespace NexNet.Inspector.Tests.Findings;

/// <summary>
/// The findings detectors: one targeted scenario per detector (the findings fixtures for a slow call, a window stall and
/// an orphan result; crafted sessions for the rest), no findings on a clean capture, ranking, and live (incremental)
/// detection equal to a run over the finished session.
/// </summary>
public class FindingsEngineTests
{
    private const ushort Pipe = 1 | (2 << 8);

    private static CaptureSession Open(FixtureKind kind, int index = 0, NexusSchema? workspace = null)
    {
        var recording = RecordingFile.Open(SessionFixtures.Path(kind));
        var session = recording.CreateSession(recording.Sessions[index].SessionId, workspace);
        recording.Index(session);
        return session;
    }

    private static string Lines(IEnumerable<Finding> findings) => string.Join("\n", findings.Select(f => FindingsText.Format(f)));

    [Test]
    [Arguments(FixtureKind.ChatClient)]
    [Arguments(FixtureKind.ChatClientVariant)]
    public async Task A_clean_capture_has_no_findings(FixtureKind kind)
    {
        var findings = FindingsEngine.Analyze(Open(kind));
        await Assert.That(findings).IsEmpty().Because(Lines(findings));
    }

    [Test]
    public async Task Slow_fixture_finds_its_slow_call_as_a_p99_outlier()
    {
        var session = Open(FixtureKind.FindingsSlow);
        var findings = FindingsEngine.Analyze(session);
        await Assert.That(findings.Count).IsEqualTo(1).Because(Lines(findings));
        var slow = findings[0];
        await Assert.That(slow.Kind).IsEqualTo(FindingKind.SlowCall);
        await Assert.That(slow.Severity).IsEqualTo(FindingSeverity.Warning);
        // Judged as it completed: against the 12 calls before it.
        await Assert.That(slow.Message).Contains("its other 12 calls");

        // It cites the call that took longest, by the text format's ID.
        var renderer = new TextRenderer(session);
        var rows = session.Assembler.Invocations.ToArray();
        var longest = Enumerable.Range(0, rows.Length)
            .MaxBy(i => Sample.Fixtures.AttachmentMilliseconds(session.Assembler, rows[i], session.Header.StopwatchFrequency));
        await Assert.That(slow.Ids).IsEquivalentTo([renderer.InvocationId(longest)]);
        await Assert.That(slow.EndMs - slow.StartMs).IsGreaterThanOrEqualTo(Sample.Fixtures.FindingsSlowMs * 0.9);
    }

    [Test]
    public async Task Slow_threshold_flags_a_call_without_a_distribution()
    {
        var findings = FindingsEngine.Analyze(Open(FixtureKind.FindingsSlow), new FindingsOptions { SlowCallMs = 100, OutlierMinSamples = 1000 });
        await Assert.That(findings.Count).IsEqualTo(1);
        await Assert.That(findings[0].Message).Contains("(slow threshold 100.0ms)");
        await Assert.That(FindingsEngine.Analyze(Open(FixtureKind.FindingsSlow), new FindingsOptions { OutlierMinSamples = 1000 })).IsEmpty();
    }

    [Test]
    public async Task Stall_fixture_finds_window_stalls_on_its_upload_pipe()
    {
        var session = Open(FixtureKind.FindingsStall);
        var findings = FindingsEngine.Analyze(session);
        await Assert.That(findings.Count).IsGreaterThanOrEqualTo(1).Because(Lines(findings));
        await Assert.That(findings.All(f => f.Kind == FindingKind.WindowStall && f.Severity == FindingSeverity.Warning)).IsTrue().Because(Lines(findings));
        var pipe = new TextRenderer(session).PipeId(0);
        await Assert.That(findings.All(f => f.Ids[0] == pipe)).IsTrue();
        var first = findings.Single(f => f.Message.Contains("waited"));
        await Assert.That(first.EndMs - first.StartMs).IsGreaterThanOrEqualTo(50);
        await Assert.That(first.Message).Contains("16.0KB window");
    }

    [Test]
    public async Task Orphan_fixture_finds_the_result_of_a_call_made_before_the_capture()
    {
        var findings = FindingsEngine.Analyze(Open(FixtureKind.FindingsOrphan));
        await Assert.That(findings.Count).IsEqualTo(1).Because(Lines(findings));
        await Assert.That(findings[0].Kind).IsEqualTo(FindingKind.OrphanResult);
        await Assert.That(findings[0].Severity).IsEqualTo(FindingSeverity.Info);
        await Assert.That(findings[0].Message).Contains("before the capture attached");
        await Assert.That(findings[0].Ids[0]).StartsWith("#");
    }

    [Test]
    public async Task An_orphan_result_in_a_capture_from_inception_is_an_error()
    {
        var crafted = new CraftedSession().Handshake()
            .At(5, s => s.Invocation(TapDirection.Outbound, 1, 7, InvocationFlags.None, CraftedSession.IntArgument))
            .At(6, s => s.Result(TapDirection.Inbound, 1, InvocationResultState.CompletedResult, CraftedSession.Nil))
            .At(9, s => s.Result(TapDirection.Inbound, 9, InvocationResultState.CompletedResult, CraftedSession.Nil))
            .Close(20);
        var findings = FindingsEngine.Analyze(crafted.Session);
        await Assert.That(findings.Count).IsEqualTo(1).Because(Lines(findings));
        await Assert.That(findings[0].Kind).IsEqualTo(FindingKind.OrphanResult);
        await Assert.That(findings[0].Severity).IsEqualTo(FindingSeverity.Error);
        await Assert.That(findings[0].Ids).IsEquivalentTo(["#9"]);
    }

    [Test]
    public async Task Gaps_are_findings_citing_the_frame_before_them()
    {
        var session = Open(FixtureKind.ChatServer);
        var gaps = FindingsEngine.Analyze(session).Where(f => f.Kind == FindingKind.Gap).ToArray();
        await Assert.That(gaps.Length).IsEqualTo(session.Gaps.Length);
        await Assert.That(gaps.All(g => g.Severity == FindingSeverity.Warning && g.Ids[0].StartsWith('f') && g.Message.Contains("(Drop)"))).IsTrue();
    }

    [Test]
    public async Task A_truncated_capture_says_it_was_cut_off()
    {
        var findings = FindingsEngine.Analyze(Open(FixtureKind.Truncated, 2));
        await Assert.That(findings.Any(f => f.Kind == FindingKind.Disconnect && f.Severity == FindingSeverity.Info && f.Message.Contains("cut off"))).IsTrue()
            .Because(Lines(findings));
    }

    [Test]
    public async Task A_schema_that_differs_from_the_workspace_is_a_finding()
    {
        var findings = FindingsEngine.Analyze(Open(FixtureKind.LegacySchema, 0, NexusSchema.Of<ChatServerNexus>()));
        var schema = findings.Single(f => f.Kind == FindingKind.SchemaMismatch);
        await Assert.That(schema.Severity).IsEqualTo(FindingSeverity.Warning);
        await Assert.That(schema.Ids[0]).StartsWith("@0x");
        await Assert.That(FindingsEngine.Analyze(Open(FixtureKind.LegacySchema)).Any(f => f.Kind == FindingKind.SchemaMismatch)).IsFalse();
    }

    [Test]
    public async Task Malformed_frames_and_a_protocol_error_disconnect_are_errors()
    {
        var crafted = new CraftedSession().Handshake()
            .Raw(TapDirection.Inbound, 5, [0xEE, 0xEE, 0xEE, 0xEE])
            .Close(10, DisconnectReason.ProtocolError);
        var findings = FindingsEngine.Analyze(crafted.Session);
        await Assert.That(findings.Any(f => f.Kind == FindingKind.ProtocolError && f.Severity == FindingSeverity.Error && f.Ids[0].StartsWith('f')))
            .IsTrue().Because(Lines(findings));
        await Assert.That(findings.Any(f => f.Kind == FindingKind.Disconnect && f.Severity == FindingSeverity.Error && f.Message.Contains("ProtocolError")))
            .IsTrue().Because(Lines(findings));
    }

    [Test]
    public async Task A_handshake_that_never_completed_is_an_error()
    {
        var crafted = new CraftedSession();
        crafted.Raw(TapDirection.Outbound, 0, CraftedSession.Preamble).Raw(TapDirection.Inbound, 0, CraftedSession.Preamble)
            .Close(5, DisconnectReason.Timeout);
        var findings = FindingsEngine.Analyze(crafted.Session);
        await Assert.That(findings.Any(f => f.Kind == FindingKind.ProtocolError && f.Message.Contains("handshake"))).IsTrue().Because(Lines(findings));
        await Assert.That(findings.Any(f => f.Kind == FindingKind.Disconnect && f.Severity == FindingSeverity.Warning)).IsTrue();
    }

    [Test]
    public async Task A_result_after_the_cancellation_is_a_cancel_race()
    {
        var crafted = new CraftedSession().Handshake()
            .At(1, s => s.Invocation(TapDirection.Outbound, 3, 7, InvocationFlags.None, CraftedSession.IntArgument))
            .At(10, s => s.Cancellation(TapDirection.Outbound, 3))
            .At(20, s => s.Result(TapDirection.Inbound, 3, InvocationResultState.CompletedResult, CraftedSession.Nil))
            .Close(30);
        var findings = FindingsEngine.Analyze(crafted.Session);
        await Assert.That(findings.Count).IsEqualTo(1).Because(Lines(findings));
        await Assert.That(findings[0].Kind).IsEqualTo(FindingKind.CancelRace);
        await Assert.That(findings[0].Severity).IsEqualTo(FindingSeverity.Warning);
        await Assert.That(findings[0].Ids).IsEquivalentTo(["#3"]);
        await Assert.That(findings[0].Message).Contains("10.0ms later");
    }

    [Test]
    public async Task Calls_pending_and_pipes_open_at_disconnect_are_findings()
    {
        var crafted = new CraftedSession().Handshake()
            .At(1, s => s.Invocation(TapDirection.Outbound, 4, 7, InvocationFlags.None, CraftedSession.IntArgument))
            .At(2, s => s.Invocation(TapDirection.Outbound, 0, 8, InvocationFlags.IgnoreReturn | InvocationFlags.DuplexPipe, (ref MsgPackWriter w) =>
            {
                w.WriteArrayHeader(1);
                w.Write((byte)1);
            }))
            .At(3, s => s.PipeState(TapDirection.Inbound, Pipe, PipeStateFlags.Ready, 4096))
            .Close(50);
        var findings = FindingsEngine.Analyze(crafted.Session);
        var pending = findings.Single(f => f.Kind == FindingKind.PendingAtEnd);
        await Assert.That(pending.Ids).IsEquivalentTo(["#4"]);
        await Assert.That(pending.Severity).IsEqualTo(FindingSeverity.Warning);
        await Assert.That(pending.Message).Contains("at disconnect");
        var pipe = findings.Single(f => f.Kind == FindingKind.PipeOpenAtEnd);
        await Assert.That(pipe.Ids).IsEquivalentTo(["⇄1·2", "#0"]);
        await Assert.That(pipe.Severity).IsEqualTo(FindingSeverity.Warning);

        // Still open when the capture (not the session) ended: only worth knowing.
        var stopped = new CraftedSession().Handshake()
            .At(1, s => s.Invocation(TapDirection.Outbound, 4, 7, InvocationFlags.None, CraftedSession.IntArgument))
            .Truncate();
        var later = FindingsEngine.Analyze(stopped.Session).Single(f => f.Kind == FindingKind.PendingAtEnd);
        await Assert.That(later.Severity).IsEqualTo(FindingSeverity.Info);
    }

    [Test]
    public async Task A_writer_out_of_window_until_an_update_stalls()
    {
        var crafted = new CraftedSession().Handshake()
            .At(1, s => s.Invocation(TapDirection.Outbound, 0, 8, InvocationFlags.IgnoreReturn | InvocationFlags.DuplexPipe, (ref MsgPackWriter w) =>
            {
                w.WriteArrayHeader(1);
                w.Write((byte)1);
            }))
            .At(2, s => s.PipeState(TapDirection.Inbound, Pipe, PipeStateFlags.Ready, 4096))
            .At(10, s => s.PipeWrite(TapDirection.Outbound, Pipe, new byte[4096]))
            .At(110, s => s.WindowUpdate(TapDirection.Inbound, Pipe, 4096))
            .At(111, s => s.PipeWrite(TapDirection.Outbound, Pipe, new byte[100]))
            .At(120, s => s.PipeState(TapDirection.Outbound, Pipe, PipeStateFlags.Complete, 0).PipeState(TapDirection.Inbound, Pipe, PipeStateFlags.Complete, 0))
            .Close(130);
        var findings = FindingsEngine.Analyze(crafted.Session);
        await Assert.That(findings.Count).IsEqualTo(1).Because(Lines(findings));
        await Assert.That(findings[0].Kind).IsEqualTo(FindingKind.WindowStall);
        await Assert.That(findings[0].Ids[0]).IsEqualTo("⇄1·2");
        await Assert.That(findings[0].Message).Contains("waited 100.0ms");

        // A shorter wait is no stall.
        var quick = FindingsEngine.Analyze(crafted.Session, new FindingsOptions { StallMs = 150 });
        await Assert.That(quick).IsEmpty();
    }

    [Test]
    public async Task A_caller_out_of_invocation_credit_stalls()
    {
        var crafted = new CraftedSession().Handshake(credits: 2)
            .At(1, s => s.Invocation(TapDirection.Outbound, 1, 7, InvocationFlags.IgnoreReturn, CraftedSession.IntArgument))
            .At(2, s => s.Invocation(TapDirection.Outbound, 2, 7, InvocationFlags.IgnoreReturn, CraftedSession.IntArgument))
            .At(100, s => CraftedSession.Credit(s, TapDirection.Inbound, 1))
            .At(101, s => s.Invocation(TapDirection.Outbound, 3, 7, InvocationFlags.IgnoreReturn, CraftedSession.IntArgument))
            .Close(110);
        var findings = FindingsEngine.Analyze(crafted.Session);
        var stall = findings.Single(f => f.Kind == FindingKind.CreditStall);
        await Assert.That(stall.Message).Contains("all 2 the peer allows");
        await Assert.That(stall.EndMs - stall.StartMs).IsGreaterThanOrEqualTo(97);
    }

    [Test]
    public async Task A_large_argument_near_the_receivers_limit_is_a_warning_and_chunk_storms_are_noted()
    {
        var arguments = WireScript.Body((ref MsgPackWriter w) =>
        {
            w.WriteArrayHeader(1);
            w.WriteBinary(new byte[90_000]);
        });
        var inner = WireScript.InvocationBody(5, 7, InvocationFlags.None, (ref MsgPackWriter w) => w.WriteRaw(arguments));
        var crafted = new CraftedSession().Handshake(maxMessageSize: 100_000)
            .At(1, s => s.MessageStart(TapDirection.Outbound, 5, LargeMessageKind.Argument, inner.Length));
        for (var offset = 0; offset < inner.Length; offset += 20_000)
        {
            var chunk = inner.AsSpan(offset, Math.Min(20_000, inner.Length - offset)).ToArray();
            crafted.At(2 + (offset / 20_000), s => s.Chunk(TapDirection.Outbound, 5, LargeMessageKind.Argument, chunk));
        }

        crafted.At(20, s => s.Result(TapDirection.Inbound, 5, InvocationResultState.CompletedResult, CraftedSession.Nil)).Close(30);
        var findings = FindingsEngine.Analyze(crafted.Session, new FindingsOptions { ChunkStormFrames = 4 });
        var large = findings.Single(f => f.Kind == FindingKind.LargeMessage);
        await Assert.That(large.Severity).IsEqualTo(FindingSeverity.Warning);
        await Assert.That(large.Ids).IsEquivalentTo(["#5"]);
        await Assert.That(large.Message).Contains("of the receiver's MaxMessageSize");
        var storm = findings.Single(f => f.Kind == FindingKind.ChunkStorm);
        await Assert.That(storm.Ids).IsEquivalentTo(["#5"]);

        // Without the limit in reach, a message over the large threshold is only noted.
        var noted = FindingsEngine.Analyze(crafted.Session, new FindingsOptions { NearLimitFraction = 2, LargeMessageBytes = 50_000 });
        await Assert.That(noted.Single(f => f.Kind == FindingKind.LargeMessage).Severity).IsEqualTo(FindingSeverity.Info);
    }

    [Test]
    public async Task A_ping_answered_far_later_than_usual_is_an_outlier()
    {
        var crafted = new CraftedSession().Handshake();
        for (var i = 0; i < 10; i++)
        {
            crafted.At(10 + (i * 10), s => s.Ping(TapDirection.Outbound));
            crafted.At(11 + (i * 10), s => s.Ping(TapDirection.Inbound));
        }

        crafted.At(200, s => s.Ping(TapDirection.Outbound)).At(500, s => s.Ping(TapDirection.Inbound)).Close(600);
        var findings = FindingsEngine.Analyze(crafted.Session);
        await Assert.That(findings.Count).IsEqualTo(1).Because(Lines(findings));
        await Assert.That(findings[0].Kind).IsEqualTo(FindingKind.PingRtt);
        await Assert.That(findings[0].Message).Contains("ping answered after 300.0ms, median 1.0ms");
        await Assert.That(findings[0].Ids.Length).IsEqualTo(2);
    }

    [Test]
    public async Task Sessions_that_keep_failing_and_reconnecting_are_a_reconnect_loop()
    {
        var sessions = new List<CaptureSession>();
        for (var i = 0; i < 4; i++)
        {
            var crafted = new CraftedSession(id: i + 1, remote: $"10.0.0.2:{5000 + i}", startMs: i * 1000)
                .Handshake(i * 1000, reconnect: i > 0)
                .Close((i * 1000) + 500, i < 3 ? DisconnectReason.SocketError : DisconnectReason.Graceful);
            sessions.Add(crafted.Session);
        }

        var findings = FindingsEngine.AnalyzeAcross(sessions);
        await Assert.That(findings.Count).IsEqualTo(1).Because(Lines(findings));
        await Assert.That(findings[0].Kind).IsEqualTo(FindingKind.ReconnectLoop);
        await Assert.That(findings[0].Ids).IsEquivalentTo(["@0x2", "@0x3", "@0x4"]);
        await Assert.That(findings[0].Message).Contains("3 reconnections in 2.00s from 10.0.0.2");

        // Graceful sessions of different clients are no loop.
        var recording = RecordingFile.Open(SessionFixtures.Path(FixtureKind.ServerAll));
        var all = recording.Sessions.Select(o =>
        {
            var s = recording.CreateSession(o.SessionId);
            recording.Index(s);
            return s;
        }).ToList();
        await Assert.That(FindingsEngine.AnalyzeAcross(all)).IsEmpty();
    }

    [Test]
    public async Task Findings_rank_errors_first_then_warnings_then_infos_each_by_time()
    {
        var crafted = new CraftedSession().Handshake()
            .At(1, s => s.Invocation(TapDirection.Outbound, 3, 7, InvocationFlags.None, CraftedSession.IntArgument))
            .At(2, s => s.Cancellation(TapDirection.Outbound, 3))
            .At(3, s => s.Result(TapDirection.Inbound, 3, InvocationResultState.CompletedResult, CraftedSession.Nil))
            .At(4, s => s.Invocation(TapDirection.Outbound, 4, 7, InvocationFlags.None, CraftedSession.IntArgument))
            .Gap(TapDirection.Inbound, 5, 6)
            .At(8, s => s.Result(TapDirection.Inbound, 9, InvocationResultState.CompletedResult, CraftedSession.Nil))
            .Raw(TapDirection.Outbound, 9, [0xEE, 0, 0])
            .Truncate();
        var findings = FindingsEngine.Analyze(crafted.Session);
        var severities = findings.Select(f => f.Severity).ToArray();
        await Assert.That(severities).IsEquivalentTo(severities.OrderByDescending(s => s).ToArray()).Because(Lines(findings));
        await Assert.That(severities.Distinct().Count()).IsEqualTo(3).Because(Lines(findings));
        foreach (var group in findings.GroupBy(f => f.Severity))
        {
            var times = group.Select(f => f.StartMs).ToArray();
            await Assert.That(times).IsEquivalentTo(times.Order().ToArray());
        }

        await Assert.That(findings.Select(f => f.Kind).Take(2).ToArray()).IsEquivalentTo([FindingKind.OrphanResult, FindingKind.ProtocolError]);
    }

    [Test]
    public async Task Live_updates_find_the_same_as_a_run_over_the_finished_session()
    {
        static void Script(CraftedSession crafted)
        {
            crafted.Handshake(credits: 2);
            for (var i = 0; i < 12; i++)
            {
                var id = (ushort)(10 + i);
                var ms = 10.0 + (i * 2);
                crafted.At(ms, s => s.Invocation(TapDirection.Outbound, id, 7, InvocationFlags.None, CraftedSession.IntArgument));
                crafted.At(ms + (i == 9 ? 0.9 : 0.1), s => CraftedSession.Credit(s, TapDirection.Inbound, 1)
                    .Result(TapDirection.Inbound, id, InvocationResultState.CompletedResult, CraftedSession.Nil));
            }

            crafted.At(40, s => s.Invocation(TapDirection.Outbound, 30, 7, InvocationFlags.None, CraftedSession.IntArgument));
            crafted.At(250, s => s.Result(TapDirection.Inbound, 30, InvocationResultState.CompletedResult, CraftedSession.Nil));
            crafted.At(260, s => s.Invocation(TapDirection.Outbound, 31, 7, InvocationFlags.None, CraftedSession.IntArgument));
            crafted.Gap(TapDirection.Outbound, 261, 262);
            crafted.At(263, s => s.Result(TapDirection.Inbound, 77, InvocationResultState.CompletedResult, CraftedSession.Nil));
            crafted.Close(300);
        }

        var finished = new CraftedSession();
        Script(finished);
        var whole = FindingsEngine.Analyze(finished.Session);
        await Assert.That(whole.Select(f => f.Kind).Order().ToArray())
            .IsEquivalentTo(new[] { FindingKind.Gap, FindingKind.OrphanResult, FindingKind.PendingAtEnd, FindingKind.SlowCall }).Because(Lines(whole));

        // The same session fed step by step, the engine updating after every step.
        var live = new CraftedSession();
        var engine = new FindingsEngine(live.Session);
        var found = new List<Finding>();
        var updates = 0;
        live.AfterStep = () => updates += engine.Update(found) >= 0 ? 1 : 0;
        Script(live);
        await Assert.That(updates).IsGreaterThan(20);
        await Assert.That(engine.IsComplete).IsTrue();
        await Assert.That(engine.Update(found)).IsEqualTo(0);
        FindingsEngine.Rank(found);
        await Assert.That(Lines(found)).IsEqualTo(Lines(whole));
    }

    [Test]
    public async Task The_engine_reports_slow_calls_while_the_session_is_live()
    {
        var crafted = new CraftedSession().Handshake();
        var engine = new FindingsEngine(crafted.Session);
        var found = new List<Finding>();
        for (var i = 0; i < 10; i++)
        {
            var id = (ushort)(10 + i);
            crafted.At(10 + i, s => s.Invocation(TapDirection.Outbound, id, 7, InvocationFlags.None, CraftedSession.IntArgument)
                .Result(TapDirection.Inbound, id, InvocationResultState.CompletedResult, CraftedSession.Nil));
        }

        crafted.At(30, s => s.Invocation(TapDirection.Outbound, 40, 7, InvocationFlags.None, CraftedSession.IntArgument));
        await Assert.That(engine.Update(found)).IsEqualTo(0);
        crafted.At(230, s => s.Result(TapDirection.Inbound, 40, InvocationResultState.CompletedResult, CraftedSession.Nil));
        await Assert.That(engine.Update(found)).IsEqualTo(1);
        await Assert.That(found[0].Kind).IsEqualTo(FindingKind.SlowCall);
        await Assert.That(found[0].Ids).IsEquivalentTo(["#40"]);
        await Assert.That(engine.IsComplete).IsFalse();

        crafted.Close(240);
        await Assert.That(engine.Update(found)).IsEqualTo(0);
        await Assert.That(engine.IsComplete).IsTrue();
    }

    [Test]
    public async Task Findings_over_the_per_kind_limit_are_counted_in_one_more_finding()
    {
        var crafted = new CraftedSession().Handshake();
        for (var i = 0; i < 5; i++)
        {
            var id = (ushort)(10 + i);
            crafted.At(1 + i, s => s.Invocation(TapDirection.Outbound, id, 7, InvocationFlags.None, CraftedSession.IntArgument));
        }

        crafted.Close(20);
        var findings = FindingsEngine.Analyze(crafted.Session, new FindingsOptions { MaxPerKind = 2 });
        var pending = findings.Where(f => f.Kind == FindingKind.PendingAtEnd).ToArray();
        await Assert.That(pending.Length).IsEqualTo(3);
        await Assert.That(pending.Count(f => f.Message == "3 more pending findings not listed")).IsEqualTo(1);
    }
}
