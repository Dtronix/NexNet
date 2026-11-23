using NexNet.Inspector.App.Controls.Compare;
using NexNet.Inspector.Core.Compare;
using NexNet.Inspector.Core.Model;

namespace NexNet.Inspector.App.Tests.Compare;

/// <summary>
/// The aligned view's rows on the Chat captures: a header and a Transport heat row per file, then each category
/// collapsed (16 px heat, 18 px packed) or expanded into its groups, the selected entity pinned under its category in
/// both files, "Only differences" and the sidebar's checkboxes; the heat rows' density; the shared window.
/// </summary>
public sealed class CompareRowsModelTests
{
    [Test]
    public async Task Each_file_has_a_header_and_transport_then_its_categories_with_pipes_open()
    {
        await using CompareFixture f = await CompareFixture.OpenAsync();
        CompareRowsModel rows = f.Compare.Rows;
        CompareRow[] all = rows.Rows.ToArray();
        CompareRow[] headers = all.Where(r => r.Kind == CompareRowKind.FileHeader).ToArray();

        using (Assert.Multiple())
        {
            await Assert.That(headers.Length).IsEqualTo(2);
            await Assert.That(headers[0].Side).IsEqualTo(CompareSide.A);
            await Assert.That(headers[0].Label).IsEqualTo(f.A.Title);
            await Assert.That(headers[1].Label).IsEqualTo(f.B.Title);
            await Assert.That(headers[0].Height).IsEqualTo(CompareRowsModel.HeaderHeight);
            await Assert.That(all[1]).IsEqualTo(all[1] with { Kind = CompareRowKind.Collapsed, Category = CompareCategory.Transport, Height = CompareRowsModel.HeatHeight });
            await Assert.That(all.Any(r => r is { Kind: CompareRowKind.Expanded, Category: CompareCategory.Pipes })).IsTrue();
            await Assert.That(all.Count(r => r is { Kind: CompareRowKind.Group, Category: CompareCategory.Pipes, Side: CompareSide.A }))
                .IsEqualTo(rows.GroupsOf(CompareCategory.Pipes).Count);
            await Assert.That(all.Where(r => r is { Kind: CompareRowKind.Collapsed, Category: not CompareCategory.Transport }).All(r => r.Height == CompareRowsModel.HeatHeight)).IsTrue();
            await Assert.That(rows.Height).IsEqualTo(all.Sum(r => r.Height));
        }

        // Tops are contiguous, so RowAt finds every row from its middle.
        for (int i = 0; i < all.Length; i++)
            await Assert.That(rows.RowAt(all[i].Top + (all[i].Height / 2))).IsEqualTo(i);
    }

    [Test]
    public async Task Expanding_and_packing_change_the_rows_of_both_files()
    {
        await using CompareFixture f = await CompareFixture.OpenAsync();
        CompareRowsModel rows = f.Compare.Rows;
        int changes = 0;
        rows.Changed += c => changes += (c & CompareChanges.Rows) != 0 ? 1 : 0;

        rows.ToggleExpanded(CompareCategory.Pipes);
        await Assert.That(rows.Rows.ToArray().Any(r => r.Kind == CompareRowKind.Group)).IsFalse();

        rows.SetExpanded(CompareCategory.Invocations, true);
        await Assert.That(rows.Rows.ToArray().Any(r => r is { Kind: CompareRowKind.Group, Category: CompareCategory.Invocations }))
            .IsFalse().Because("invocation groups start unticked");
        CompareFixture.ShowGroups(rows, CompareCategory.Invocations);
        int invocationGroups = rows.GroupsOf(CompareCategory.Invocations).Count;
        await Assert.That(rows.Rows.ToArray().Count(r => r is { Kind: CompareRowKind.Group, Category: CompareCategory.Invocations }))
            .IsEqualTo(invocationGroups * 2);

        f.Compare.RowMode.Value = CompareRowMode.Packed;
        CompareRow[] packed = rows.Rows.ToArray();
        using (Assert.Multiple())
        {
            await Assert.That(packed.Where(r => r is { Kind: CompareRowKind.Collapsed, Category: not CompareCategory.Transport }).All(r => r.Height == CompareRowsModel.PackedHeight)).IsTrue();
            // Transport stays the session at a glance.
            await Assert.That(packed.Where(r => r.Category == CompareCategory.Transport && r.Kind == CompareRowKind.Collapsed).All(r => r.Height == CompareRowsModel.HeatHeight)).IsTrue();
            // Fold, expand, one per group ticked, pack.
            await Assert.That(changes).IsEqualTo(3 + invocationGroups);
        }
    }

    [Test]
    public async Task The_selected_entity_is_pinned_under_its_category_in_both_files()
    {
        await using CompareFixture f = await CompareFixture.OpenAsync();
        CompareSession session = f.Compare.Session!;
        int attachment = Capture.CompareScreen.FindInvocation(session, "SendAttachment");
        f.Compare.Select(attachment);

        CompareRow[] all = f.Compare.Rows.Rows.ToArray();
        CompareRow[] pinned = all.Where(r => r.Kind == CompareRowKind.Pinned).ToArray();
        using (Assert.Multiple())
        {
            await Assert.That(pinned.Length).IsEqualTo(2);
            await Assert.That(pinned[0].Label).StartsWith("Inv #").And.EndsWith("SendAttachment");
            await Assert.That(pinned[0].Count).EndsWith(" ms");
            // Right under the Invocations row of each file.
            await Assert.That(all[Array.IndexOf(all, pinned[0]) - 1]).IsEqualTo(all[Array.IndexOf(all, pinned[0]) - 1] with { Category = CompareCategory.Invocations, Kind = CompareRowKind.Collapsed });
            await Assert.That(f.Compare.Detail?.Index).IsEqualTo(attachment);
            await Assert.That(f.Compare.DetailSubtitle).StartsWith("Invocation #");
        }

        f.Compare.Select(-1);
        await Assert.That(f.Compare.Rows.Rows.ToArray().Any(r => r.Kind == CompareRowKind.Pinned)).IsFalse();
        await Assert.That(f.Compare.Detail).IsNull();
    }

    [Test]
    public async Task Only_differences_keeps_rows_that_differ_and_the_checkboxes_hide_groups()
    {
        await using CompareFixture f = await CompareFixture.OpenAsync();
        CompareRowsModel rows = f.Compare.Rows;
        CompareSession session = f.Compare.Session!;
        rows.SetExpanded(CompareCategory.Invocations, true);
        CompareFixture.ShowGroups(rows, CompareCategory.Invocations);
        int before = rows.Rows.Length;

        f.Compare.OnlyDifferences = true;
        CompareRow[] filtered = rows.Rows.ToArray();
        using (Assert.Multiple())
        {
            await Assert.That(filtered.Length).IsLessThanOrEqualTo(before);
            await Assert.That(filtered.Where(r => r.Kind == CompareRowKind.Group).All(r => session.Groups[r.Group].Differing > 0)).IsTrue();
            await Assert.That(filtered.Where(r => r.Kind is CompareRowKind.Collapsed or CompareRowKind.Expanded && r.Category != CompareCategory.Transport)
                .All(r => r.HasDelta)).IsTrue();
        }

        f.Compare.OnlyDifferences = false;
        CompareRow group = rows.Rows.ToArray().First(r => r.Kind == CompareRowKind.Group);
        rows.SetGroupShown(group.Group, false);
        using (Assert.Multiple())
        {
            await Assert.That(rows.Rows.ToArray().Any(r => r.Kind == CompareRowKind.Group && r.Group == group.Group)).IsFalse();
            await Assert.That(rows.Rows.Length).IsEqualTo(before - 2);
        }
    }

    [Test]
    public async Task Heat_density_counts_every_frame_once_per_category()
    {
        await using CompareFixture f = await CompareFixture.OpenAsync();
        CompareDensity density = f.Compare.Rows.DensityA;
        CompareCategory[] categories = [CompareCategory.Invocations, CompareCategory.Pipes, CompareCategory.Collections, CompareCategory.Control, CompareCategory.Handshake];
        int split = categories.Sum(density.Total);

        using (Assert.Multiple())
        {
            await Assert.That(density.Total(CompareCategory.Transport)).IsEqualTo(f.A.FrameCount);
            await Assert.That(split).IsEqualTo(f.A.FrameCount);
            await Assert.That(density.Count(CompareCategory.Transport, long.MinValue, long.MaxValue)).IsEqualTo(f.A.FrameCount);
            await Assert.That(density.Count(CompareCategory.Transport, 5, 5)).IsEqualTo(0);
        }

        // Halves of the capture add up: [from, mid) and [mid, to) never count a frame twice.
        TimeWindow range = f.A.TimeRange;
        long mid = range.Center;
        int halves = density.Count(CompareCategory.Transport, range.Start, mid) + density.Count(CompareCategory.Transport, mid, range.End + 1);
        await Assert.That(halves).IsEqualTo(f.A.FrameCount);
    }

    [Test]
    public async Task The_window_zooms_and_pans_inside_the_compare()
    {
        await using CompareFixture f = await CompareFixture.OpenAsync();
        CompareRowsModel rows = f.Compare.Rows;
        TimeWindow range = rows.Bounds;
        await Assert.That(rows.Window).IsEqualTo(range);
        await Assert.That(f.Compare.Subtitle).StartsWith("whole session · ");

        rows.ZoomAround(range.Center, 0.25);
        using (Assert.Multiple())
        {
            await Assert.That(rows.Window.Duration).IsEqualTo(range.Duration / 4).Within(2);
            await Assert.That(f.Compare.Subtitle).Contains(" of ");
        }

        rows.Pan(-range.Duration);
        await Assert.That(rows.Window.Start).IsEqualTo(range.Start);
        rows.ZoomAround(range.Center, 100);
        await Assert.That(rows.Window).IsEqualTo(range);
    }
}
