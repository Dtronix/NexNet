using NexNet.Inspector.Core.Compare;
using NexNet.Inspector.Core.Format;

namespace NexNet.Inspector.Tests.Compare;

/// <summary>The alignment math of each mode, on synthetic headers.</summary>
public class CompareClockTests
{
    private const long Second = TimeSpan.TicksPerSecond;

    // A file recorded at 10 MHz whose anchor read wall clock W at Stopwatch tick 1,000,000.
    private static NntlFileHeader Header(long frequency, long wallUtcTicks, long ticksAtAnchor)
        => new(NntlFormat.Version, wallUtcTicks, frequency, ticksAtAnchor, "test", "1", null);

    [Test]
    public async Task Relative_starts_both_documents_at_zero()
    {
        var a = Header(10_000_000, 0, 0);
        var b = Header(3_000_000, 0, 0);
        var clock = CompareClock.Create(Alignment.Relative, a, 50_000_000, null, b, 9_000_000, null);

        await Assert.That(clock.ToCompare(CompareSide.A, 50_000_000)).IsEqualTo(0);
        await Assert.That(clock.ToCompare(CompareSide.B, 9_000_000)).IsEqualTo(0);
        await Assert.That(clock.OffsetA).IsEqualTo(0);
        await Assert.That(clock.OffsetB).IsEqualTo(0);
        await Assert.That(clock.AnchorTime).IsEqualTo(0);

        // One second after each start, whatever the frequency.
        await Assert.That(clock.ToCompare(CompareSide.A, 60_000_000)).IsEqualTo(Second);
        await Assert.That(clock.ToCompare(CompareSide.B, 12_000_000)).IsEqualTo(Second);
        await Assert.That(clock.ToTicks(CompareSide.B, Second)).IsEqualTo(12_000_000);
        await Assert.That(clock.ToTicks(CompareSide.A, 2 * Second)).IsEqualTo(70_000_000);
    }

    [Test]
    public async Task Wall_clock_keeps_the_real_time_between_the_documents()
    {
        var wall = new DateTime(2026, 11, 22, 12, 0, 0, DateTimeKind.Utc).Ticks;

        // A: anchor at tick 1,000,000 (10 MHz); session starts 1 s after the anchor.
        // B: another machine at 2 MHz, anchored 3 s later in real time; its session starts 0.5 s after its anchor,
        // i.e. 2.5 s after A's start.
        var a = Header(10_000_000, wall, 1_000_000);
        var b = Header(2_000_000, wall + (3 * Second), 777);
        var clock = CompareClock.Create(Alignment.WallClock, a, 11_000_000, null, b, 777 + 1_000_000, null);

        await Assert.That(clock.OffsetA).IsEqualTo(0);
        await Assert.That(clock.OffsetB).IsEqualTo(2 * Second + (Second / 2));
        await Assert.That(clock.ToCompare(CompareSide.B, 777 + 1_000_000)).IsEqualTo(clock.OffsetB);
        await Assert.That(clock.AnchorTime).IsEqualTo(0);

        // The same UTC instant maps to the same compare time on both sides.
        var utc = wall + (4 * Second);
        var ticksA = 1_000_000 + (4 * 10_000_000);
        var ticksB = 777 + (1 * 2_000_000);
        await Assert.That(clock.ToCompare(CompareSide.A, ticksA)).IsEqualTo(clock.ToCompare(CompareSide.B, ticksB));
        await Assert.That(clock.ToCompare(CompareSide.A, ticksA)).IsEqualTo(utc - wall - Second);

        // B started first (2.5 s before its anchor, so 0.5 s before A's start): zero moves to B's start.
        var early = CompareClock.Create(Alignment.WallClock, a, 11_000_000, null, b, 777 - 5_000_000, null);
        await Assert.That(early.OffsetB).IsEqualTo(0);
        await Assert.That(early.OffsetA).IsEqualTo(Second / 2);
    }

    [Test]
    public async Task On_event_pins_both_events_to_one_instant_counted_from_the_earlier_start()
    {
        var a = Header(10_000_000, 0, 0);
        var b = Header(10_000_000, 0, 0);

        // A's event 3 s after its start, B's 1 s after its start: B is moved 2 s right.
        var clock = CompareClock.Create(Alignment.OnEvent, a, 100 * 10_000_000L, 103 * 10_000_000L, b, 500, 500 + 10_000_000);
        await Assert.That(clock.EventResolved).IsTrue();
        await Assert.That(clock.AnchorTime).IsEqualTo(3 * Second);
        await Assert.That(clock.ToCompare(CompareSide.A, 103 * 10_000_000L)).IsEqualTo(3 * Second);
        await Assert.That(clock.ToCompare(CompareSide.B, 500 + 10_000_000)).IsEqualTo(3 * Second);
        await Assert.That(clock.OffsetA).IsEqualTo(0);
        await Assert.That(clock.OffsetB).IsEqualTo(2 * Second);
    }

    [Test]
    public async Task On_event_missing_from_a_document_falls_back_to_its_start()
    {
        var a = Header(10_000_000, 0, 0);
        var clock = CompareClock.Create(Alignment.OnEvent, a, 1000, 1000 + 10_000_000, a, 5000, null);
        await Assert.That(clock.EventResolved).IsFalse();
        await Assert.That(clock.ToCompare(CompareSide.B, 5000)).IsEqualTo(clock.AnchorTime);
        await Assert.That(clock.OffsetB).IsEqualTo(Second);
    }

    [Test]
    public async Task Tick_conversion_does_not_overflow_on_long_uptimes()
    {
        // A Stopwatch at 1 GHz after ~292 years of uptime would overflow a plain multiply by 10^7.
        const long frequency = 1_000_000_000;
        var ticks = long.MaxValue / 4;
        var converted = CompareClock.ToTimeSpanTicks(ticks, frequency);
        await Assert.That(converted).IsEqualTo(ticks / 100);
        await Assert.That(CompareClock.FromTimeSpanTicks(converted, frequency)).IsEqualTo(converted * 100);
    }
}
