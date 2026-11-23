using NexNet.Inspector.App.Controls.Ladder;
using NexNet.Inspector.Core.Model;
using NexNet.Messages;
using static NexNet.Inspector.App.Tests.Ladder.LadderDataBuilder;

namespace NexNet.Inspector.App.Tests.Ladder;

/// <summary>
/// The ladder's row layout (OQ6): compact rows are 18 px one after another; time-scaled rows add a gap above each row,
/// 12 px per tenfold of the time since the row before above 10 µs, capped at 48 px; appending lays out only the new rows.
/// </summary>
public sealed class LadderLayoutTests
{
    [Test]
    public async Task Compact_rows_are_18_px_one_after_another()
    {
        LadderLayout layout = new();
        LadderModel model = Busy(1_000);
        layout.Update(model, LadderMode.Compact);

        await Assert.That(layout.Count).IsEqualTo(1_000);
        await Assert.That(layout.TotalHeight).IsEqualTo(18_000);
        await Assert.That(layout.Top(37)).IsEqualTo(37 * 18);
        await Assert.That(layout.Height(37)).IsEqualTo(18);
        await Assert.That(layout.Gap(37)).IsEqualTo(0);
        await Assert.That(layout.IndexAt(0)).IsEqualTo(0);
        await Assert.That(layout.IndexAt((37 * 18) + 17.9)).IsEqualTo(37);
        await Assert.That(layout.IndexAt(1e9)).IsEqualTo(999);
        await Assert.That(layout.IndexAt(-5)).IsEqualTo(0);
    }

    [Test]
    [Arguments(0.0, 0.0)]
    [Arguments(0.005, 0.0)]
    [Arguments(0.01, 0.0)]
    [Arguments(0.1, 12.0)]
    [Arguments(1.0, 24.0)]
    [Arguments(10.0, 36.0)]
    [Arguments(100.0, 48.0)]
    [Arguments(60_000.0, 48.0)]
    public async Task Time_scaled_gaps_grow_with_the_log_of_the_time_between_rows_and_are_capped(double ms, double gap)
    {
        await Assert.That(LadderLayout.GapFor(Ticks(ms), Frequency)).IsEqualTo(gap);
    }

    [Test]
    public async Task Time_scaled_gaps_never_shrink_as_time_grows_and_stay_within_0_to_48_px()
    {
        double previous = 0;
        for (double ms = 0.001; ms < 1e6; ms *= 1.37)
        {
            double gap = LadderLayout.GapFor(Ticks(ms), Frequency);
            await Assert.That(gap).IsGreaterThanOrEqualTo(previous);
            await Assert.That(gap).IsBetween(LadderLayout.MinGap, LadderLayout.MaxGap);
            previous = gap;
        }

        // A row that finished before the previous one (the other direction) gets no gap.
        await Assert.That(LadderLayout.GapFor(-Ticks(5), Frequency)).IsEqualTo(0);
    }

    [Test]
    public async Task Time_scaled_rows_take_their_gap_above_them()
    {
        LadderDataBuilder b = new();
        b.Row(TapDirection.Inbound, 0, MessageType.Ping)
            .Row(TapDirection.Inbound, 0.001, MessageType.Ping)
            .Row(TapDirection.Outbound, 1.001, MessageType.Ping)
            .Row(TapDirection.Inbound, 5_000, MessageType.Ping);
        LadderLayout layout = new();
        layout.Update(b.Build(), LadderMode.TimeScaled);

        await Assert.That(layout.Gap(0)).IsEqualTo(0);
        await Assert.That(layout.Gap(1)).IsEqualTo(0);
        await Assert.That(layout.Gap(2)).IsEqualTo(24);
        await Assert.That(layout.Gap(3)).IsEqualTo(48);
        await Assert.That(layout.Top(2)).IsEqualTo(36);
        await Assert.That(layout.Height(2)).IsEqualTo(42);
        await Assert.That(layout.Top(3)).IsEqualTo(78);
        await Assert.That(layout.TotalHeight).IsEqualTo(78 + 66);

        // A point in a row's gap belongs to that row.
        await Assert.That(layout.IndexAt(37)).IsEqualTo(2);
        await Assert.That(layout.IndexAt(77.9)).IsEqualTo(2);
        await Assert.That(layout.IndexAt(78)).IsEqualTo(3);
    }

    [Test]
    public async Task Appended_rows_are_laid_out_as_a_full_layout_would_and_a_mode_switch_starts_over()
    {
        LadderModel all = Busy(3_000);
        LadderLayout full = new();
        full.Update(all, LadderMode.TimeScaled);

        LadderDataBuilder growing = new();
        LadderLayout incremental = new();
        ReadOnlySpan<LadderRow> rows = all.Rows;
        for (int i = 0; i < rows.Length; i++)
        {
            growing.Model.Add(rows[i]);
            if (i % 250 == 0 || i == rows.Length - 1)
            {
                growing.Model.NotifyChanged();
                incremental.Update(growing.Model, LadderMode.TimeScaled);
            }
        }

        await Assert.That(incremental.Count).IsEqualTo(full.Count);
        await Assert.That(incremental.TotalHeight).IsEqualTo(full.TotalHeight);
        for (int i = 0; i < full.Count; i += 97)
            await Assert.That(incremental.Top(i)).IsEqualTo(full.Top(i));

        // The busy clock pauses 25 ms every 64 rows: time-scaled is taller than compact, compact is 18 px a row again.
        await Assert.That(full.TotalHeight).IsGreaterThan(3_000 * 18);
        await Assert.That(full.Update(all, LadderMode.TimeScaled)).IsFalse();
        await Assert.That(full.Update(all, LadderMode.Compact)).IsTrue();
        await Assert.That(full.TotalHeight).IsEqualTo(3_000 * 18);

        // A cleared model (another document) lays out from the start.
        all.Clear();
        all.NotifyChanged();
        full.Update(all, LadderMode.Compact);
        await Assert.That(full.Count).IsEqualTo(0);
        await Assert.That(full.IndexAt(10)).IsEqualTo(-1);
    }
}
