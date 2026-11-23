using NexNet.Inspector.App.Controls.DataTable.FieldDiff;
using NexNet.Inspector.App.Controls.Primitives;
using NexNet.Inspector.App.Model;
using NexNet.Inspector.App.ViewModels;
using NexNet.Inspector.Core.Compare;

namespace NexNet.Inspector.App.Tests.Compare;

/// <summary>
/// The field diff data-table variant over I30's field diffs: each compared value's heading then its fields in
/// pre-order, differing rows tinted with B's value flagged, a dash for the side without a field, and the pane's header
/// texts (entity, part, difference count, file columns).
/// </summary>
public sealed class FieldDiffTableTests
{
    [Test]
    public async Task Rows_list_each_part_heading_then_its_fields_in_pre_order()
    {
        CompareSession compare = CompareFixture.Variant();
        EntityFieldDiff diff = compare.GetFieldDiff(CompareFixture.Sends(compare)[0].Index);
        FieldDiffTableSource source = new();
        source.Set(diff);

        await Assert.That(source.Count).IsEqualTo(diff.Parts.Length + diff.Rows.Length);
        await Assert.That(source[0]).IsEqualTo(new FieldDiffEntry(FieldDiffEntryKind.Part, 0, -1));
        await Assert.That(source[1]).IsEqualTo(new FieldDiffEntry(FieldDiffEntryKind.Field, 0, 0));
        for (int row = 0; row < diff.Rows.Length; row++)
        {
            int index = source.IndexOfRow(row);
            await Assert.That(source[index].Row).IsEqualTo(row);
            await Assert.That(source[index].Part).IsEqualTo(diff.Rows[row].Part);
        }

        await Assert.That(source.Format(0).Name).IsEqualTo("Arguments");
        await Assert.That(source.IndexOfRow(diff.Rows.Length)).IsEqualTo(-1);
        source.Set(null);
        await Assert.That(source.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Differing_fields_are_tinted_and_show_both_values()
    {
        CompareSession compare = CompareFixture.Variant();
        EntityFieldDiff diff = compare.GetFieldDiff(CompareFixture.Sends(compare)[0].Index);
        FieldDiffTableSource source = new();
        source.Set(diff);

        int differing = 0;
        for (int i = 0; i < source.Count; i++)
        {
            FieldDiffRowVisual visual = (FieldDiffRowVisual)source.CreateVisual();
            source.Prepare(visual, i);
            bool differs = source[i].Kind == FieldDiffEntryKind.Field && diff.Rows[source[i].Row].Kind != FieldDiffKind.Same;
            await Assert.That(source.Differs(i)).IsEqualTo(differs);
            await Assert.That(visual.Band).IsEqualTo(differs ? ThemeBrush.DiffBand : null);
            if (differs)
                differing++;
        }

        await Assert.That(differing).IsEqualTo(diff.DifferenceCount);

        FieldDiffCells text = source.Format(source.IndexOfRow(CompareFixture.TextRow(diff)));
        await Assert.That(text.Name).IsEqualTo("Text");
        await Assert.That(text.A).IsEqualTo("\"hello everyone\"");
        await Assert.That(text.B).IsEqualTo("\"" + Sample.Fixtures.VariantHelloText + "\"");
        await Assert.That(text.Diff).IsEqualTo(FieldDiffKind.Changed);
        await Assert.That(text.Depth).IsGreaterThan(1);
    }

    [Test]
    public async Task A_field_only_one_document_has_is_a_dash_on_the_other_side()
    {
        CompareSession compare = CompareFixture.Variant();
        EntityFieldDiff diff = compare.GetFieldDiff(CompareFixture.Sends(compare)[1].Index);
        FieldDiffTableSource source = new();
        source.Set(diff);

        FieldDiffCells heading = source.Format(0);
        await Assert.That(heading.A).IsNull();
        await Assert.That(heading.B).IsEqualTo("");
        for (int i = 1; i < source.Count; i++)
        {
            FieldDiffCells cells = source.Format(i);
            if (cells.Kind == FieldDiffEntryKind.Part)
                continue;
            await Assert.That(cells.A).IsNull();
            await Assert.That(cells.B).IsNotNull();
            await Assert.That(cells.Diff).IsEqualTo(FieldDiffKind.Added);
            await Assert.That(source.Differs(i)).IsTrue();
        }

        FieldDiffRowVisual visual = (FieldDiffRowVisual)source.CreateVisual();
        source.Prepare(visual, 1);
        await Assert.That(visual.AccessibleName!).Contains("only in B");
        await Assert.That(visual.ToolTipAt(FieldDiffColumns.Field)).IsEqualTo(diff.Rows[0].Path);
    }

    [Test]
    public async Task Texts_name_fields_parts_and_counts()
    {
        await Assert.That(FieldDiffText.FieldName("message.Text")).IsEqualTo("Text");
        await Assert.That(FieldDiffText.FieldName("Items[2]")).IsEqualTo("[2]");
        await Assert.That(FieldDiffText.FieldName("a.b[3]")).IsEqualTo("[3]");
        await Assert.That(FieldDiffText.FieldName("[0]")).IsEqualTo("[0]");
        await Assert.That(FieldDiffText.FieldName("message")).IsEqualTo("message");
        await Assert.That(FieldDiffText.Differences(0)).IsEqualTo("No differences");
        await Assert.That(FieldDiffText.Differences(1)).IsEqualTo("1 difference");
        await Assert.That(FieldDiffText.Differences(1204)).IsEqualTo("1,204 differences");
        FieldDiffPart item = new(FieldDiffPartKind.ChannelItem, WireFlow.ServerToClient, 2, null, null, 0, 0, 0);
        await Assert.That(FieldDiffText.PartLabel(item)).IsEqualTo("Item 3 · server → client");
        await Assert.That(FieldDiffText.PartLabel(item with { Kind = FieldDiffPartKind.Result })).IsEqualTo("Result");
    }

    [Test]
    public async Task Columns_head_the_file_columns_with_their_names_and_colours()
    {
        IReadOnlyList<Controls.DataTable.DataTableColumn> columns = FieldDiffColumns.For("chat-repro.nntl", "server-all.nntl › 0x3A21");
        await Assert.That(columns.Count).IsEqualTo(3);
        await Assert.That(columns[FieldDiffColumns.Field].Header).IsEqualTo("Field");
        await Assert.That(columns[FieldDiffColumns.Field].Swatch).IsNull();
        await Assert.That(columns[FieldDiffColumns.A].Header).IsEqualTo("chat-repro.nntl");
        await Assert.That(columns[FieldDiffColumns.A].Swatch).IsEqualTo(ThemeBrush.DirectionInbound);
        await Assert.That(columns[FieldDiffColumns.B].Swatch).IsEqualTo(ThemeBrush.DirectionOutbound);
    }

    [Test]
    public async Task The_view_model_names_the_entity_and_follows_the_selected_part()
    {
        CompareSession compare = CompareFixture.Variant();
        EntityMatch changed = CompareFixture.Sends(compare)[0];
        FieldDiffViewModel model = new();
        int raised = 0;
        model.SelectionChanged += () => raised++;

        model.Show(compare, changed.Index);
        int id = compare.A.Assembler.Invocations[changed.A.Index].InvocationId;
        await Assert.That(model.Subtitle).IsEqualTo($"Invocation #{id} SendMessage");
        await Assert.That(model.DifferenceText).IsEqualTo(FieldDiffText.Differences(model.Rows.Diff!.DifferenceCount));
        await Assert.That(model.HasDifferences).IsTrue();
        await Assert.That(model.SelectedRow).IsEqualTo(-1);
        await Assert.That(raised).IsEqualTo(1);

        int text = CompareFixture.TextRow(model.Rows.Diff!);
        model.SelectRow(text);
        await Assert.That(model.SelectedRow).IsEqualTo(text);
        await Assert.That(model.SelectedPart).IsEqualTo(0);
        await Assert.That(model.Subtitle).IsEqualTo($"Invocation #{id} SendMessage · Arguments");
        await Assert.That(raised).IsEqualTo(2);

        model.SetFiles("chat-client.nntl", "chat-client-variant.nntl");
        await Assert.That(model.Columns[FieldDiffColumns.B].Header).IsEqualTo("chat-client-variant.nntl");

        model.Show(null, -1);
        await Assert.That(model.Rows.Count).IsEqualTo(0);
        await Assert.That(model.Subtitle).IsEqualTo(FieldDiffText.NoEntity);
        await Assert.That(model.DifferenceText).IsEqualTo("");
        await Assert.That(model.EmptyText).IsEqualTo(FieldDiffViewModel.NoEntityText);
        await Assert.That(raised).IsEqualTo(3);
    }

    [Test]
    public async Task An_entity_its_documents_agree_on_has_no_differences()
    {
        CompareSession compare = CompareFixture.Mirrored();
        EntityMatch attachment = CompareFixture.First(compare, "SendAttachment", CompareEntityKind.Invocation);
        FieldDiffViewModel model = new();
        model.Show(compare, attachment.Index);

        await Assert.That(model.HasDifferences).IsFalse();
        await Assert.That(model.DifferenceText).IsEqualTo("No differences");
        await Assert.That(model.Rows.Count).IsGreaterThan(0);
        for (int i = 0; i < model.Rows.Count; i++)
            await Assert.That(model.Rows.Differs(i)).IsFalse();
    }
}
