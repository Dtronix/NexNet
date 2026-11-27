using NexNet.Inspector.App.Controls.Hex;
using NexNet.Inspector.App.ViewModels;
using NexNet.Inspector.Core.Compare;
using NexNet.Inspector.Core.Framing;

namespace NexNet.Inspector.App.Tests.Compare;

/// <summary>
/// The A ⇄ B hex model: each document's frame from its header, rows interleaved A then B, header fills kept and bodies
/// unfilled, the selected field ringed on both sides, differing bytes marked and counted, a byte picking its field back,
/// and an entity only one document holds showing that document alone.
/// </summary>
public sealed class CompareHexModelTests
{
    [Test]
    public async Task Rows_interleave_A_and_B_from_the_frame_header()
    {
        CompareSession compare = CompareFixture.Variant();
        EntityFieldDiff diff = compare.GetFieldDiff(CompareFixture.Sends(compare)[0].Index);
        CompareHexModel model = new();
        int changed = 0;
        model.Changed += () => changed++;
        model.Load(compare, diff);

        await Assert.That(changed).IsEqualTo(1);
        await Assert.That(model.Part).IsEqualTo(0);
        await Assert.That(model.HasSide(CompareSide.A) && model.HasSide(CompareSide.B)).IsTrue();
        model.TryGetFrame(CompareSide.A, out FrameRecord a);
        model.TryGetFrame(CompareSide.B, out FrameRecord b);
        int rowsA = (a.Length + 15) / 16;
        int rowsB = (b.Length + 15) / 16;
        await Assert.That(model.LineCount).IsEqualTo(rowsA + rowsB);
        await Assert.That(model.GetLine(0)).IsEqualTo(new CompareHexLine(CompareSide.A, 0));
        await Assert.That(model.GetLine(1)).IsEqualTo(new CompareHexLine(CompareSide.B, 0));
        await Assert.That(model.GetLine(2)).IsEqualTo(new CompareHexLine(CompareSide.A, 1));
        for (int line = 0; line < model.LineCount; line++)
        {
            CompareHexLine at = model.GetLine(line);
            await Assert.That(model.LineOf(at.Side, at.Row)).IsEqualTo(line);
        }

        HexRow row = default;
        model.BuildRow(0, ref row);
        await Assert.That(row.Row).IsEqualTo(0);
        await Assert.That(row.Count).IsEqualTo(Math.Min(16, a.Length));
        await Assert.That(row.Roles[0]).IsEqualTo(HexCellRole.Type);
        await Assert.That(row.Roles[1]).IsEqualTo(HexCellRole.Length);
        await Assert.That(row.Roles[a.HeaderLength + a.PostHeaderLength]).IsEqualTo(HexCellRole.None);
        await Assert.That(model.GetBytes(CompareSide.A)[0]).IsEqualTo((byte)a.Type);
        await Assert.That(model.Digits).IsEqualTo(CompareHexModel.MinDigits);
        await Assert.That(model.Subtitle).IsEqualTo(a.Length > CompareHexModel.PreviewBytes ? "header + body start" : "header + body");
    }

    [Test]
    public async Task The_selected_field_is_ringed_in_both_documents()
    {
        CompareSession compare = CompareFixture.Variant();
        EntityFieldDiff diff = compare.GetFieldDiff(CompareFixture.Sends(compare)[0].Index);
        int text = CompareFixture.TextRow(diff);
        CompareHexModel model = new();
        model.Load(compare, diff, row: text);

        await Assert.That(model.Row).IsEqualTo(text);
        (int startA, int endA) = model.GetSelected(CompareSide.A);
        (int startB, int endB) = model.GetSelected(CompareSide.B);
        await Assert.That(endA - startA).IsEqualTo((int)diff.Rows[text].RangeA.Length);
        await Assert.That(endB - startB).IsEqualTo((int)diff.Rows[text].RangeB.Length);
        await Assert.That(startA).IsEqualTo(startB);

        // The ringed bytes are the field's bytes.
        var valueA = diff.GetValue(diff.Rows[text], CompareSide.A)!;
        await Assert.That(model.GetBytes(CompareSide.A)[startA..endA].SequenceEqual(valueA.GetBytes(valueA.Fields[diff.Rows[text].FieldA]))).IsTrue();

        int ringed = 0;
        for (int line = 0; line < model.LineCount; line++)
        {
            HexRow row = default;
            model.BuildRow(line, ref row);
            CompareHexLine at = model.GetLine(line);
            (int start, int end) = model.GetSelected(at.Side);
            for (int cell = 0; cell < row.Count; cell++)
            {
                long position = row.Row + cell;
                bool selected = (row.Marks[cell] & HexCellMarks.Selected) != 0;
                await Assert.That(selected).IsEqualTo(position >= start && position < end);
                if (selected)
                    ringed++;
            }
        }

        await Assert.That(ringed).IsEqualTo((endA - startA) + (endB - startB));
    }

    [Test]
    public async Task Differing_bytes_are_marked_and_agreeing_documents_have_none()
    {
        CompareSession variant = CompareFixture.Variant();
        EntityFieldDiff changed = variant.GetFieldDiff(CompareFixture.Sends(variant)[0].Index);
        CompareHexModel model = new();
        model.Load(variant, changed, row: CompareFixture.TextRow(changed));
        await Assert.That(model.DifferingBytes).IsGreaterThan(0);

        int marked = 0;
        byte[] a = model.GetBytes(CompareSide.A).ToArray();
        byte[] b = model.GetBytes(CompareSide.B).ToArray();
        for (int line = 0; line < model.LineCount; line++)
        {
            HexRow row = default;
            model.BuildRow(line, ref row);
            CompareHexLine at = model.GetLine(line);
            byte[] own = at.Side == CompareSide.A ? a : b;
            byte[] other = at.Side == CompareSide.A ? b : a;
            for (int cell = 0; cell < row.Count; cell++)
            {
                int position = (int)row.Row + cell;
                bool differs = position >= other.Length || other[position] != own[position];
                await Assert.That((row.Marks[cell] & HexCellMarks.Differs) != 0).IsEqualTo(differs);
                if (differs && at.Side == CompareSide.A)
                    marked++;
            }
        }

        await Assert.That(marked).IsLessThanOrEqualTo(model.DifferingBytes);

        CompareSession mirrored = CompareFixture.Mirrored();
        EntityMatch attachment = CompareFixture.First(mirrored, "SendAttachment", CompareEntityKind.Invocation);
        model.Load(mirrored, mirrored.GetFieldDiff(attachment.Index));
        await Assert.That(model.HasBytes).IsTrue();
        await Assert.That(model.DifferingBytes).IsEqualTo(0);
        await Assert.That(model.GetBytes(CompareSide.A).SequenceEqual(model.GetBytes(CompareSide.B))).IsTrue();
    }

    [Test]
    public async Task A_byte_picks_its_field_and_a_header_byte_none()
    {
        CompareSession compare = CompareFixture.Variant();
        EntityFieldDiff diff = compare.GetFieldDiff(CompareFixture.Sends(compare)[0].Index);
        int text = CompareFixture.TextRow(diff);
        CompareHexModel model = new();
        model.Load(compare, diff, row: text);

        (int start, int end) = model.GetSelected(CompareSide.B);
        await Assert.That(model.FindRow(CompareSide.B, end - 1)).IsEqualTo(text);
        await Assert.That(model.FindRow(CompareSide.A, model.GetSelected(CompareSide.A).Start)).IsEqualTo(text);
        await Assert.That(model.FindRow(CompareSide.A, 0)).IsEqualTo(-1);
        await Assert.That(model.FindRow(CompareSide.A, 1 << 20)).IsEqualTo(-1);
    }

    [Test]
    public async Task An_entity_only_one_document_holds_shows_that_document_alone()
    {
        CompareSession compare = CompareFixture.Variant();
        EntityFieldDiff diff = compare.GetFieldDiff(CompareFixture.Sends(compare)[1].Index);
        CompareHexModel model = new();
        model.Load(compare, diff, row: 0);

        await Assert.That(model.HasSide(CompareSide.A)).IsFalse();
        await Assert.That(model.HasSide(CompareSide.B)).IsTrue();
        await Assert.That(model.DifferingBytes).IsEqualTo(0);
        for (int line = 0; line < model.LineCount; line++)
        {
            await Assert.That(model.GetLine(line).Side).IsEqualTo(CompareSide.B);
            HexRow row = default;
            model.BuildRow(line, ref row);
            for (int cell = 0; cell < row.Count; cell++)
                await Assert.That(row.Marks[cell] & HexCellMarks.Differs).IsEqualTo(HexCellMarks.None);
        }

        await Assert.That(model.GetSelected(CompareSide.B).End).IsGreaterThan(model.GetSelected(CompareSide.B).Start);
        model.Clear();
        await Assert.That(model.HasBytes).IsFalse();
        await Assert.That(model.Diff).IsNull();
    }

    [Test]
    public async Task The_detail_view_model_rings_the_row_picked_in_the_table_and_back()
    {
        CompareSession compare = CompareFixture.Variant();
        CompareDetailViewModel detail = new();
        detail.Show(compare, CompareFixture.Sends(compare)[0].Index);
        await Assert.That(detail.Hex.HasBytes).IsTrue();
        await Assert.That(detail.Hex.Model.Row).IsEqualTo(-1);
        await Assert.That(detail.Hex.Subtitle).IsEqualTo(detail.Hex.Model.Subtitle);

        int text = CompareFixture.TextRow(detail.FieldDiff.Rows.Diff!);
        detail.FieldDiff.SelectRow(text);
        await Assert.That(detail.Hex.Model.Row).IsEqualTo(text);

        detail.FieldDiff.SelectedIndex = 0;
        await Assert.That(detail.Hex.Model.Row).IsEqualTo(-1);
        int position = detail.Hex.Model.GetSelected(CompareSide.A).Start;
        detail.PickField(text);
        await Assert.That(detail.FieldDiff.SelectedRow).IsEqualTo(text);
        await Assert.That(detail.Hex.Model.GetSelected(CompareSide.A).End).IsGreaterThan(position);

        detail.Show(null, -1);
        await Assert.That(detail.Hex.HasBytes).IsFalse();
        await Assert.That(detail.Hex.Subtitle).IsEqualTo("");
    }
}
