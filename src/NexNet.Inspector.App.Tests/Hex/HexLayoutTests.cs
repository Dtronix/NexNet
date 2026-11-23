using NexNet.Inspector.App.Controls.Hex;
using NexNet.Inspector.Core.Model;
using NexNet.Inspector.Core.Session;
using static NexNet.Inspector.App.Tests.Hex.HexCaptureBuilder;

namespace NexNet.Inspector.App.Tests.Hex;

/// <summary>
/// Line generation over a session's op stores: 16-byte rows at stream offsets, a read rule above the row a new op starts
/// in, a frame annotation above each row a frame starts in, runs that share rows within a direction and restart on a
/// direction switch, the IN/OUT filter, and an incremental update that matches a fresh layout.
/// </summary>
public class HexLayoutTests
{
    private static string S<T>(IEnumerable<T> items) => string.Join(",", items);

    private static HexLine[] All(HexLayout layout)
    {
        HexLine[] lines = new HexLine[layout.LineCount];
        int n = layout.Read(0, lines);
        return lines[..n];
    }

    private static HexLayout Lay(CaptureSession session, HexDirections directions = HexDirections.Both)
    {
        HexLayout layout = new();
        lock (session.Gate)
            layout.Update(session, directions);
        return layout;
    }

    // Read #1: preamble + a 20-byte pipe write (33 bytes); read #2 continues mid-row with a 30-byte pipe write.
    private static CaptureSession TwoReads() => new HexCaptureBuilder()
        .Op(TapDirection.Inbound, 0.1, Join(Preamble(), PipeWrite(0x0102, Pattern(20))))
        .Op(TapDirection.Inbound, 0.2, PipeWrite(0x0102, Pattern(30)))
        .Session;

    [Test]
    public async Task Rows_are_16_bytes_at_stream_offsets_with_rules_at_op_boundaries()
    {
        CaptureSession session = TwoReads();
        HexLayout layout = Lay(session, HexDirections.Inbound);
        HexLine[] lines = All(layout);

        await Assert.That(layout.OpCount).IsEqualTo(2);
        await Assert.That(S(lines.Select(l => l.Kind))).IsEqualTo("Frames,Bytes,Bytes,ReadRule,Frames,Bytes,Bytes,Bytes");
        await Assert.That(S(lines.Where(l => l.Kind == HexLineKind.Bytes).Select(l => l.Row))).IsEqualTo("0,16,32,48,64");

        // The row read #2 starts in spans both reads, and marks the new read's first cell.
        HexLine shared = lines[5];
        layout.GetRowSpan(shared, out long start, out long end);
        await Assert.That((start, end)).IsEqualTo((32L, 48L));
        layout.GetReadStarts(shared, out int first, out int count);
        await Assert.That((first, count)).IsEqualTo((1, 1));
        layout.GetRowSpan(lines[7], out start, out end);
        await Assert.That((start, end)).IsEqualTo((64L, 68L));

        // Byte → line, and frame starts per row.
        await Assert.That(layout.FindLine(TapDirection.Inbound, 40)).IsEqualTo(5);
        await Assert.That(layout.FindLine(TapDirection.Inbound, 0)).IsEqualTo(1);
        await Assert.That(layout.FindLine(TapDirection.Outbound, 0)).IsEqualTo(-1);
        await Assert.That(layout.GetFrameStarts(lines[0], out _)).IsEqualTo(2);
        await Assert.That(layout.GetFrameStarts(lines[4], out _)).IsEqualTo(1);
        await Assert.That(layout.MaxOffset).IsEqualTo(68);
    }

    [Test]
    public async Task A_window_read_from_the_middle_matches_the_whole()
    {
        CaptureSession session = TwoReads();
        HexLayout layout = Lay(session);
        HexLine[] all = All(layout);
        for (int from = 0; from < all.Length; from++)
        {
            HexLine[] window = new HexLine[3];
            int n = layout.Read(from, window);
            await Assert.That(S(window[..n])).IsEqualTo(S(all.Skip(from).Take(3)));
        }

        await Assert.That(layout.Read(all.Length, new HexLine[4])).IsEqualTo(0);
    }

    [Test]
    public async Task Directions_interleave_in_op_order_and_the_filter_keeps_one()
    {
        CaptureSession session = new HexCaptureBuilder()
            .Op(TapDirection.Inbound, 0.1, Join(Preamble(), Ping(), Ping()))
            .Op(TapDirection.Outbound, 0.2, Join(Preamble(), Ping()))
            .Op(TapDirection.Inbound, 0.3, Join(Ping(), Ping()))
            .Session;

        HexLayout both = Lay(session);
        await Assert.That(both.OpCount).IsEqualTo(3);
        await Assert.That(S(Enumerable.Range(0, 3).Select(i => both.GetOp(i).Direction))).IsEqualTo("Inbound,Outbound,Inbound");

        // The third op continues the first in its stream, but after a switch it starts its own run on its own row.
        HexOp third = both.GetOp(2);
        await Assert.That(third.RunStart).IsTrue();
        HexLine[] lines = All(both);
        HexLine[] rows = lines.Where(l => l.Kind == HexLineKind.Bytes).ToArray();
        await Assert.That(S(rows.Select(r => (r.Direction, r.Row)))).IsEqualTo("(Inbound, 0),(Outbound, 0),(Inbound, 0)");
        both.GetRowSpan(rows[2], out long start, out long end);
        await Assert.That((start, end)).IsEqualTo((10L, 12L));
        await Assert.That(lines.Count(l => l.Kind == HexLineKind.ReadRule)).IsEqualTo(2);

        // Inbound only: one run, one row, one rule inside it.
        HexLayout inbound = Lay(session, HexDirections.Inbound);
        HexLine[] only = All(inbound);
        await Assert.That(inbound.OpCount).IsEqualTo(2);
        await Assert.That(inbound.GetOp(1).RunStart).IsFalse();
        await Assert.That(only.Count(l => l.Kind == HexLineKind.Bytes)).IsEqualTo(1);
        await Assert.That(only.Count(l => l.Kind == HexLineKind.ReadRule)).IsEqualTo(1);
        HexLine row = only.Single(l => l.Kind == HexLineKind.Bytes);
        inbound.GetRowSpan(row, out start, out end);
        await Assert.That((start, end)).IsEqualTo((0L, 12L));

        HexLayout outbound = Lay(session, HexDirections.Outbound);
        await Assert.That(outbound.OpCount).IsEqualTo(1);
        await Assert.That(All(outbound).All(l => l.Direction == TapDirection.Outbound)).IsTrue();
    }

    [Test]
    public async Task Updating_as_ops_arrive_matches_laying_out_once()
    {
        // A stream of frames cut into ops at awkward points (inside headers, mid-row, several ops in one row).
        byte[] stream = Join(Preamble(), PipeWrite(0x0101, Pattern(40)), Ping(), Chunk(7, 0, Pattern(70, 3)), Ping(),
            PipeWrite(0x0101, Pattern(9)), Chunk(7, 0, Pattern(33, 9)), Ping(), Ping());
        int[] cuts = [3, 5, 11, 12, 13, 31, 32, 50, 64, 65, 66, 67, 100, 141, stream.Length];
        HexCaptureBuilder builder = new();
        HexLayout incremental = new();
        int at = 0;
        double ms = 0;
        foreach (int cut in cuts)
        {
            builder.Op(TapDirection.Inbound, ms += 0.1, stream[at..cut]);
            if (cut % 2 == 0)
                builder.Op(TapDirection.Outbound, ms += 0.05, cut == 12 ? Preamble() : Ping());
            at = cut;
            lock (builder.Session.Gate)
                incremental.Update(builder.Session, HexDirections.Both);
        }

        HexLayout once = Lay(builder.Session);
        await Assert.That(incremental.LineCount).IsEqualTo(once.LineCount);
        await Assert.That(S(All(incremental))).IsEqualTo(S(All(once)));
        HexLine[] lines = All(once);
        await Assert.That(lines.Count(l => l.Kind == HexLineKind.Bytes)).IsGreaterThan(stream.Length / 16);

        // Every inbound byte is on exactly one row.
        long covered = 0;
        foreach (HexLine line in lines.Where(l => l.Kind == HexLineKind.Bytes && l.Direction == TapDirection.Inbound))
        {
            once.GetRowSpan(line, out long start, out long end);
            covered += end - start;
        }

        await Assert.That(covered).IsEqualTo(stream.Length);
    }

    [Test]
    public async Task A_new_session_or_filter_lays_out_again_and_reports_the_change()
    {
        CaptureSession session = TwoReads();
        HexLayout layout = new();
        int changes = 0;
        layout.Changed += () => changes++;
        bool changed;
        lock (session.Gate)
            changed = layout.Update(session, HexDirections.Both);
        await Assert.That(changed).IsTrue();
        lock (session.Gate)
            changed = layout.Update(session, HexDirections.Both);
        await Assert.That(changed).IsFalse();
        lock (session.Gate)
            changed = layout.Update(session, HexDirections.Outbound);
        await Assert.That(changed).IsTrue();
        await Assert.That(layout.LineCount).IsEqualTo(0);
        await Assert.That(layout.Update(null, HexDirections.Both)).IsTrue();
        await Assert.That(layout.Session).IsNull();
        await Assert.That(changes).IsEqualTo(3);
    }

    [Test]
    public async Task Rule_and_frame_annotations_name_the_ops_and_frames()
    {
        CaptureSession session = TwoReads();
        HexLayout layout = Lay(session, HexDirections.Inbound);
        HexLine[] lines = All(layout);

        string frames = HexText.Frames(layout, lines[0]);
        await Assert.That(frames).IsEqualTo("▸ Preamble #0 @0   ▸ DuplexPipeWrite #1 @8");
        string rule = HexText.Rule(layout, lines[3]);
        await Assert.That(rule).StartsWith("┃ read #1 → #2 · 35 B");
        await Assert.That(rule).EndsWith(" ms");
        await Assert.That(HexText.Frames(layout, lines[4])).IsEqualTo("▸ DuplexPipeWrite #2 @21");

        // A switch of direction names the direction instead of the previous op.
        CaptureSession mixed = new HexCaptureBuilder()
            .Op(TapDirection.Inbound, 0.1, Join(Preamble(), Ping()))
            .Op(TapDirection.Outbound, 0.2, Join(Preamble(), Ping()))
            .Session;
        HexLayout both = Lay(mixed);
        HexLine outboundRule = All(both).Single(l => l.Kind == HexLineKind.ReadRule);
        await Assert.That(HexText.Rule(both, outboundRule)).StartsWith("┃ ▲ write #1 · 9 B");
    }
}
