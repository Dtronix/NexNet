using NexNet.Inspector.App.Controls.Hex;
using NexNet.Inspector.App.Model;
using NexNet.Inspector.Core.Format;
using NexNet.Inspector.Core.Model;
using NexNet.Inspector.Core.Session;
using static NexNet.Inspector.App.Tests.Hex.HexCaptureBuilder;

namespace NexNet.Inspector.App.Tests.Hex;

/// <summary>
/// Cell fills by byte role (preamble/ping, type, length, post-header, body; other-frame and body-selected once a frame is
/// selected), redacted cells, read-start borders, rings, and the formatted text of a row.
/// </summary>
public class HexRowTests
{
    private static HexRow[] Rows(CaptureSession session, HexHighlight? highlight = null, HexDirections directions = HexDirections.Both)
    {
        HexLayout layout = new();
        lock (session.Gate)
        {
            layout.Update(session, directions);
            HexLine[] lines = new HexLine[layout.LineCount];
            layout.Read(0, lines);
            return lines.Where(l => l.Kind == HexLineKind.Bytes).Select(l =>
            {
                HexRow row = default;
                HexRowBuilder.Build(layout, l, highlight, ref row);
                return row;
            }).ToArray();
        }
    }

    private static string Roles(in HexRow row)
    {
        ReadOnlySpan<HexCellRole> roles = row.Roles;
        return string.Join(",", roles.ToArray().Skip(row.First).Take(row.Count));
    }

    // Preamble (0-7), ping (8), MessageChunk of invocation 812 (9: type, 10-11: length, 12-14: post-header, body from 15),
    // then a pipe write.
    private static CaptureSession Chunked() => new HexCaptureBuilder()
        .Op(TapDirection.Inbound, 0.1, Join(Preamble(), Ping(), Chunk(0x032C, 0, Pattern(20))))
        .Op(TapDirection.Inbound, 0.2, PipeWrite(0x0103, Pattern(8)))
        .Session;

    [Test]
    public async Task Cells_fill_by_byte_role()
    {
        HexRow[] rows = Rows(Chunked());
        string expected = string.Join(",", Enumerable.Repeat("Preamble", 9).Concat(["Type", "Length", "Length", "PostHeader", "PostHeader", "PostHeader", "Body"]));
        await Assert.That(Roles(rows[0])).IsEqualTo(expected);
        // Row 2 holds the end of the chunk body and the start of the pipe write (read #2 at 35).
        await Assert.That(Roles(rows[2])).IsEqualTo("Body,Body,Body,Type,Length,Length,PostHeader,PostHeader,Body,Body,Body,Body,Body,Body,Body,Body");
    }

    [Test]
    public async Task Selecting_a_frame_dims_the_others_and_marks_its_body_and_type()
    {
        CaptureSession session = Chunked();
        HexHighlight highlight = new();
        new HexLinker().Build(session, Selection.OfFrame(new FrameKey(TapDirection.Inbound, 2)), FieldHover.None, null, highlight);
        await Assert.That(highlight.HasSelection).IsTrue();
        await Assert.That(highlight.Selected.Single()).IsEqualTo(new HexRange(TapDirection.Inbound, 9, 10));

        HexRow[] rows = Rows(session, highlight);
        HexCellMarks[] marks = ((ReadOnlySpan<HexCellMarks>)rows[0].Marks).ToArray();
        await Assert.That(marks[9]).IsEqualTo(HexCellMarks.Selected);
        await Assert.That(Roles(rows[0]).Split(',')[15]).IsEqualTo("BodySelected");
        // The preamble and the ping keep their fill; the pipe write is another frame.
        await Assert.That(Roles(rows[0]).Split(',')[8]).IsEqualTo("Preamble");
        await Assert.That(Roles(rows[2]).Split(',')[3..].Distinct().Single()).IsEqualTo("OtherFrame");
    }

    [Test]
    public async Task A_read_after_another_marks_its_first_cell()
    {
        HexRow[] rows = Rows(Chunked());
        HexCellMarks[] first = ((ReadOnlySpan<HexCellMarks>)rows[0].Marks).ToArray();
        HexCellMarks[] third = ((ReadOnlySpan<HexCellMarks>)rows[2].Marks).ToArray();
        await Assert.That(first[0]).IsEqualTo(HexCellMarks.None);
        await Assert.That(third[3]).IsEqualTo(HexCellMarks.ReadStart);
        await Assert.That(third.ToArray().Count(m => m != HexCellMarks.None)).IsEqualTo(1);
    }

    [Test]
    public async Task Redacted_bytes_are_marked_and_print_as_dots()
    {
        byte[] greeting = ClientGreeting(96, out int token);
        CaptureSession session = new HexCaptureBuilder()
            .Op(TapDirection.Inbound, 0.2, Join(Preamble(), greeting), new NntlByteRange(8 + token, 96))
            .Session;
        HexRow[] rows = Rows(session);
        int redacted = rows.Sum(r => ((ReadOnlySpan<HexCellMarks>)r.Marks).ToArray().Count(m => (m & HexCellMarks.Redacted) != 0));
        await Assert.That(redacted).IsEqualTo(96);

        // The first redacted byte, and the text of a fully redacted row.
        int at = 8 + token;
        HexRow row = rows[at / 16];
        await Assert.That(((ReadOnlySpan<HexCellMarks>)row.Marks)[at % 16]).IsEqualTo(HexCellMarks.Redacted);
        HexRow full = rows[(at / 16) + 2];
        char[] text = new char[HexText.LineLength(6)];
        HexText.FormatBytes(full, 6, text);
        await Assert.That(new string(text)).IsEqualTo(
            full.Row.ToString("X6") + "   " + string.Join(" ", Enumerable.Repeat("··", 16)) + "  " + new string('░', 16));
    }

    [Test]
    public async Task A_row_prints_offset_hex_and_ascii_columns()
    {
        HexRow row = Rows(Chunked())[0];
        char[] text = new char[HexText.LineLength(6)];
        HexText.FormatBytes(row, 6, text);
        await Assert.That(new string(text)).IsEqualTo("000000   4E 6E 50 14 00 00 00 01 01 72 14 00 2C 03 00 00  NnP······r··,···");

        // Cells outside the row's span stay blank, and the offset column widens for larger offsets.
        CaptureSession late = new HexCaptureBuilder(inboundStart: 0x1A3F05)
            .Op(TapDirection.Inbound, 0.1, Join(Preamble(), Ping()))
            .Session;
        HexRow partial = Rows(late)[0];
        await Assert.That((partial.Row, partial.First, partial.Count)).IsEqualTo((0x1A3F00L, 5, 9));
        HexText.FormatBytes(partial, 6, text);
        await Assert.That(new string(text)).StartsWith("1A3F00" + new string(' ', 18) + "4E 6E 50");
        await Assert.That(HexText.OffsetDigits(0x1A3F00)).IsEqualTo(6);
        await Assert.That(HexText.OffsetDigits(0x1A3F101)).IsEqualTo(7);
    }

    [Test]
    public async Task Rings_mark_the_selected_and_hovered_ranges()
    {
        CaptureSession session = Chunked();
        HexHighlight highlight = new();
        new HexLinker().Build(session, Selection.OfBytes(TapDirection.Inbound, new Core.Assembly.StreamRange(9, 6)) with
        {
            Frame = new FrameKey(TapDirection.Inbound, 2),
        }, FieldHover.None, new HexRange(TapDirection.Inbound, 10, 12), highlight);
        HexRow row = Rows(session, highlight)[0];
        HexCellMarks[] marks = ((ReadOnlySpan<HexCellMarks>)row.Marks).ToArray();
        await Assert.That(Enumerable.Range(0, 16).Where(i => (marks[i] & HexCellMarks.Selected) != 0)).IsEquivalentTo([9, 10, 11, 12, 13, 14]);
        await Assert.That(Enumerable.Range(0, 16).Where(i => (marks[i] & HexCellMarks.Hovered) != 0)).IsEquivalentTo([10, 11]);
    }
}
