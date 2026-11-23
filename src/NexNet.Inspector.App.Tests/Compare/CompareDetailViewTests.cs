using Avalonia;
using Avalonia.Controls;
using Avalonia.Styling;
using Avalonia.Threading;
using AvaloniaEdit.Rendering;
using NexNet.Inspector.App.Controls.DataTable;
using NexNet.Inspector.App.Controls.DataTable.FieldDiff;
using NexNet.Inspector.App.Controls.Hex;
using NexNet.Inspector.App.Controls.Primitives;
using NexNet.Inspector.App.ViewModels;
using NexNet.Inspector.App.Views.Compare;
using NexNet.Inspector.Core.Compare;

namespace NexNet.Inspector.App.Tests.Compare;

/// <summary>
/// The Compare bottom panel on the App's headless session: the field diff table realizes only visible rows and tints
/// the differing ones, the A ⇄ B view's document holds only the visible window with A and B rows interleaved, and a
/// click on a byte selects its field's row in the table.
/// </summary>
[NotInParallel("avalonia")]
public sealed class CompareDetailViewTests
{
    private static Task<T> With<T>(Func<CompareDetailView, CompareDetailViewModel, Window, T> body) =>
        HeadlessSession.Run(() => UiPump.InTheme(ThemeVariant.Light, () =>
        {
            CompareDetailViewModel model = new();
            model.SetFiles("chat-client.nntl", "chat-client-variant.nntl");
            CompareDetailView view = new() { DataContext = model };
            Window host = new() { Width = 1200, Height = 200, Content = view };
            host.Show();
            UiPump.Frames();
            try
            {
                return body(view, model, host);
            }
            finally
            {
                host.Close();
                Dispatcher.UIThread.RunJobs();
            }
        }));

    [Test]
    public async Task Empty_panel_shows_both_hints()
    {
        (bool tableEmpty, bool hexHint, string subtitle) = await With((view, model, _) =>
            (view.FieldDiff.Grid.State == DataTableState.Empty, view.Hex.Hex.ShowsHint, model.FieldDiff.Subtitle));
        await Assert.That(tableEmpty).IsTrue();
        await Assert.That(hexHint).IsTrue();
        await Assert.That(subtitle).IsEqualTo(Model.FieldDiffText.NoEntity);
    }

    [Test]
    public async Task Table_realizes_the_visible_rows_and_tints_the_differing_ones()
    {
        CompareSession compare = CompareFixture.Variant();
        int match = CompareFixture.Sends(compare)[0].Index;
        (int realized, int count, int tinted, int differing, string header) = await With((view, model, _) =>
        {
            model.Show(compare, match);
            UiPump.Frames();
            DataTableControl table = view.FieldDiff.Grid;
            int tint = 0, differs = 0;
            foreach (Control container in table.GetRealizedContainers())
            {
                if (container is not DataTableRow row || row.Visual is not FieldDiffRowVisual visual)
                    continue;
                if (visual.Band == ThemeBrush.DiffBand)
                    tint++;
                if (model.FieldDiff.Rows.Differs(visual.Index))
                    differs++;
            }

            return (table.RealizedCount, model.FieldDiff.Rows.Count, tint, differs, view.FieldDiff.CountText.Text ?? "");
        });

        await Assert.That(realized).IsGreaterThan(0);
        await Assert.That(realized).IsLessThanOrEqualTo(count);
        await Assert.That(tinted).IsEqualTo(differing);
        await Assert.That(tinted).IsGreaterThan(0);
        await Assert.That(header).EndsWith("difference" + (header.StartsWith("1 ", StringComparison.Ordinal) ? "" : "s"));
    }

    [Test]
    public async Task Hex_document_holds_the_visible_window_of_interleaved_rows()
    {
        CompareSession compare = CompareFixture.Variant();
        int match = CompareFixture.Sends(compare)[0].Index;
        (string[] lines, int window, int visible, int total) = await With((view, model, _) =>
        {
            model.Show(compare, match);
            UiPump.Frames();
            CompareHexView hex = view.Hex.Hex;
            return (hex.Text.Split('\n'), hex.WindowLineCount, hex.VisibleLines, model.Hex.Model.LineCount);
        });

        await Assert.That(window).IsLessThanOrEqualTo(visible + 1);
        await Assert.That(window).IsEqualTo(Math.Min(total, visible + 1));
        await Assert.That(lines[0]).StartsWith("A  0000   ");
        await Assert.That(lines[1]).StartsWith("B  0000   ");
        await Assert.That(lines[0].Length).IsEqualTo(CompareHexText.LineLength(CompareHexModel.MinDigits));
    }

    [Test]
    public async Task Clicking_a_field_byte_selects_its_row()
    {
        CompareSession compare = CompareFixture.Variant();
        int match = CompareFixture.Sends(compare)[0].Index;
        (int expected, int selected, bool hit) = await With((view, model, window) =>
        {
            model.Show(compare, match);
            int text = CompareFixture.TextRow(model.FieldDiff.Rows.Diff!);
            model.FieldDiff.SelectRow(text);
            model.FieldDiff.SelectedIndex = 0;
            UiPump.Frames();

            CompareHexView hex = view.Hex.Hex;
            CompareHexModel bytes = model.Hex.Model;
            // The Text field's first byte, from a fresh load that rings it.
            bytes.Load(compare, model.FieldDiff.Rows.Diff!, row: text);
            int position = bytes.GetSelected(CompareSide.A).Start;
            bytes.Load(compare, model.FieldDiff.Rows.Diff!, part: 0);
            hex.Reveal(CompareSide.A, position / 16);
            UiPump.Frames();

            int line = bytes.LineOf(CompareSide.A, position / 16) - hex.TopLine;
            VisualLine visual = hex.TextView.GetVisualLine(line + 1) ?? throw new InvalidOperationException("the row is not on screen");
            double x = visual.GetVisualPosition(CompareHexText.CellColumn(hex.Digits, position % 16), VisualYPosition.LineTop).X + 4;
            double y = visual.VisualTop - hex.TextView.VerticalOffset + (visual.Height / 2);
            bool found = hex.TryHitTest(new Point(x, y), out CompareSide side, out int at) && side == CompareSide.A && at == position;
            hex.PickAt(side, at);
            return (text, model.FieldDiff.SelectedRow, found);
        });

        await Assert.That(hit).IsTrue();
        await Assert.That(selected).IsEqualTo(expected);
    }
}
