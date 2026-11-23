using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NexNet.Inspector.App.Controls.DataTable;
using NexNet.Inspector.App.Controls.DataTable.Events;

namespace NexNet.Inspector.App.Tests.DataTable;

/// <summary>
/// The table on the App's headless session: a million rows realize only the visible containers (also after a jump to
/// the middle), following the tail scrolls new rows into view until the user scrolls up and again once they scroll back
/// to the end, the empty and loading states, and the row states the theme sets.
/// </summary>
[NotInParallel("avalonia")]
public sealed class DataTableControlTests
{
    private const double TableHeight = 21 + (20 * 21);

    internal static Task<T> With<T>(DataTableSource source, Func<DataTableControl, T> body, double height = TableHeight, bool loading = false) =>
        HeadlessSession.Run(() => UiPump.InTheme(ThemeVariant.Light, () =>
        {
            DataTableControl table = new()
            {
                ItemsSource = source,
                Columns = EventColumns.All,
                IsLoading = loading,
                EmptyText = "nothing",
                Height = height,
                VerticalAlignment = VerticalAlignment.Top,
            };
            Window host = new() { Width = 640, Height = 600, Content = table };
            host.Show();
            UiPump.Frames();
            try
            {
                return body(table);
            }
            finally
            {
                host.Close();
                Dispatcher.UIThread.RunJobs();
            }
        }));

    private static int[] RealizedIndexes(DataTableControl table) =>
        [.. table.GetRealizedContainers().Where(c => c.IsVisible).Select(table.IndexFromContainer).Order()];

    [Test]
    public async Task A_million_rows_realize_only_the_visible_containers()
    {
        CountingSource source = new(1_000_000);
        (int atTop, int prepared, int visuals, int middle, int[] middleIndexes, double extent) = await With(source, table =>
        {
            int top = table.RealizedCount;
            table.ScrollIntoView(500_000);
            UiPump.Frames();
            return (top, source.Prepared, source.VisualsCreated, table.RealizedCount, RealizedIndexes(table), table.Scroller!.Extent.Height);
        });

        // 20 visible rows; the panel may keep a small buffer, never one container per row.
        await Assert.That(atTop).IsGreaterThanOrEqualTo(20).And.IsLessThan(60);
        await Assert.That(middle).IsLessThan(60);
        await Assert.That(visuals).IsLessThan(120);
        await Assert.That(prepared).IsLessThan(200);
        await Assert.That(middleIndexes).Contains(500_000);
        await Assert.That(extent).IsEqualTo(1_000_000 * 21.0).Within(21);
    }

    [Test]
    public async Task Following_the_tail_scrolls_to_new_rows_until_the_user_scrolls_up()
    {
        CountingSource source = new(100);
        (bool followAtEnd, bool pausedAfterScrollUp, double keptOffset, double offsetAfterAdd, bool resumed, int lastAfterResume) = await With(source, table =>
        {
            ScrollViewer scroll = table.Scroller!;
            table.FollowTail = true;
            UiPump.Frames();
            source.Append(50);
            UiPump.Frames();
            bool atEnd = RealizedIndexes(table).Contains(149) && scroll.Offset.Y + scroll.Viewport.Height >= scroll.Extent.Height - 1;

            // The user scrolls up: following stops and new rows no longer move the view.
            scroll.Offset = new Vector(0, scroll.Offset.Y - 200);
            UiPump.Frames();
            bool paused = !table.FollowTail;
            double kept = scroll.Offset.Y;
            source.Append(50);
            UiPump.Frames();
            double after = scroll.Offset.Y;

            // Back down to the end: following resumes and the next rows come into view.
            scroll.Offset = new Vector(0, scroll.Extent.Height - scroll.Viewport.Height);
            UiPump.Frames();
            bool resumedNow = table.FollowTail;
            source.Append(10);
            UiPump.Frames();
            return (atEnd, paused, kept, after, resumedNow, RealizedIndexes(table).Max());
        });

        await Assert.That(followAtEnd).IsTrue();
        await Assert.That(pausedAfterScrollUp).IsTrue();
        await Assert.That(offsetAfterAdd).IsEqualTo(keptOffset);
        await Assert.That(resumed).IsTrue();
        await Assert.That(lastAfterResume).IsEqualTo(209);
    }

    [Test]
    public async Task No_rows_show_the_hint_and_loading_shows_skeleton_bars()
    {
        (DataTableState state, bool hint, bool skeleton) Probe(DataTableControl table)
        {
            Control empty = table.GetVisualDescendants().OfType<Control>().Single(c => c.Name == "PART_Empty");
            Control bars = table.GetVisualDescendants().OfType<Control>().Single(c => c.Name == "PART_Skeleton");
            return (table.State, empty.IsVisible, bars.IsVisible);
        }

        var empty = await With(new CountingSource(0), Probe);
        var loading = await With(new CountingSource(0), Probe, loading: true);
        var rows = await With(new CountingSource(3), Probe);

        // Loading with a few rows: the bars continue below the last one.
        var indexing = await With(new CountingSource(3), Probe, loading: true);

        await Assert.That(empty).IsEqualTo((DataTableState.Empty, true, false));
        await Assert.That(loading).IsEqualTo((DataTableState.Loading, false, true));
        await Assert.That(rows).IsEqualTo((DataTableState.Rows, false, false));
        await Assert.That(indexing).IsEqualTo((DataTableState.Rows, false, true));
        await Assert.That(DataTableSkeleton.BarsFor(4 + (4 * 24), 20)).IsEqualTo(4);
    }

    [Test]
    public async Task Rows_take_the_trace_tint_hover_and_selection_from_the_theme()
    {
        (RowLook traced, RowLook hovered, RowLook selected, bool ring, bool bar, Color?[] tokens) = await With(EventRowArray.Sample(), table =>
        {
            Color?[] expected =
            [
                UiPump.Token("Brush.Hex.BodySelected", ThemeVariant.Light),
                UiPump.Token("Brush.Surface.Raised", ThemeVariant.Light),
                UiPump.Token("Brush.State.Selected", ThemeVariant.Light),
            ];
            table.SelectedIndex = 3;
            UiPump.Frames();
            table.Preview(hoverIndex: 1, focusIndex: -1);
            UiPump.Frames();
            DataTableRow Row(int i) => (DataTableRow)table.ContainerFromIndex(i)!;
            RowLook Of(int i) => new(Row(i).Background, Row(i).IsTraced);
            RowLook t = Of(0), h = Of(1), s = Of(3);
            table.Preview(hoverIndex: -1, focusIndex: 3);
            UiPump.Frames();
            return (t, h, s, Row(3).ShowsFocusRing, Row(3).IsSelected, expected);
        });

        await Assert.That(UiPump.ColorOf(traced.Brush)).IsEqualTo(tokens[0]);
        await Assert.That(traced.Traced).IsTrue();
        await Assert.That(UiPump.ColorOf(hovered.Brush)).IsEqualTo(tokens[1]);
        await Assert.That(UiPump.ColorOf(selected.Brush)).IsEqualTo(tokens[2]);
        await Assert.That(ring).IsTrue();
        await Assert.That(bar).IsTrue();
    }

    private readonly record struct RowLook(IBrush? Brush, bool Traced);
}
