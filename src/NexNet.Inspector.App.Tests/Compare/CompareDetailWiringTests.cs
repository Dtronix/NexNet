using NexNet.Inspector.App.Controls.DataTable.FieldDiff;
using NexNet.Inspector.App.Model;
using NexNet.Inspector.Core.Compare;

namespace NexNet.Inspector.App.Tests.Compare;

/// <summary>
/// The Compare workspace drives its bottom panel: picking an entity shows its field diff (headed by the two tabs'
/// names, titled as the workspace titles it) and its A ⇄ B bytes; re-aligning keeps the picked field; clearing the
/// pick or unticking a document empties the panel.
/// </summary>
public sealed class CompareDetailWiringTests
{
    [Test]
    public async Task Picking_an_entity_fills_the_field_diff_and_hex_and_clearing_empties_them()
    {
        await using CompareFixture f = await CompareFixture.OpenAsync();
        CompareSession session = f.Compare.Session!;
        int message = Capture.CompareScreen.FindInvocation(session, "SendMessage");
        f.Compare.Select(message);

        var panel = f.Compare.DetailPanel;
        using (Assert.Multiple())
        {
            await Assert.That(panel.FieldDiff.Compare).IsSameReferenceAs(session);
            await Assert.That(panel.FieldDiff.Match).IsEqualTo(message);
            await Assert.That(panel.FieldDiff.Subtitle).IsEqualTo(f.Compare.DetailSubtitle);
            await Assert.That(panel.FieldDiff.HasDifferences).IsTrue();
            await Assert.That(panel.FieldDiff.Columns[FieldDiffColumns.A].Header).IsEqualTo(f.A.Title);
            await Assert.That(panel.FieldDiff.Columns[FieldDiffColumns.B].Header).IsEqualTo(f.B.Title);
            await Assert.That(panel.Hex.HasBytes).IsTrue();
            await Assert.That(panel.Hex.Model.DifferingBytes).IsGreaterThan(0);
        }

        // A field picked survives a re-alignment (content does not change with timing).
        int text = CompareFixture.TextRow(panel.FieldDiff.Rows.Diff!);
        panel.FieldDiff.SelectRow(text);
        f.Compare.Align.Value = Alignment.Relative;
        await Assert.That(panel.FieldDiff.SelectedRow).IsEqualTo(text);
        await Assert.That(panel.Hex.Model.Row).IsEqualTo(text);
        await Assert.That(panel.Hex.Model.GetSelected(CompareSide.B).End).IsGreaterThan(panel.Hex.Model.GetSelected(CompareSide.B).Start);

        f.Compare.Select(-1);
        await Assert.That(panel.FieldDiff.Match).IsEqualTo(-1);
        await Assert.That(panel.FieldDiff.Subtitle).IsEqualTo(FieldDiffText.NoEntity);
        await Assert.That(panel.Hex.HasBytes).IsFalse();

        f.Compare.Select(message);
        await Assert.That(panel.Hex.HasBytes).IsTrue();
        f.B.IsIncludedInCompare = false;
        await Assert.That(f.Compare.Session).IsNull();
        await Assert.That(panel.FieldDiff.Rows.Count).IsEqualTo(0);
        await Assert.That(panel.Hex.Model.HasBytes).IsFalse();
    }
}
