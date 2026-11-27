using NexNet.Inspector.Core.Format;
using NexNet.Inspector.Core.Model;
using NexNet.Inspector.Core.Session;

namespace NexNet.Inspector.Core.Compare;

/// <summary>The event an <see cref="Alignment.OnEvent"/> alignment pins both documents to.</summary>
/// <param name="Kind">What it aligns on.</param>
/// <param name="Match">For <see cref="AlignmentEventKind.Entity"/>, the entity's index in <see cref="CompareSession.Matches"/>.</param>
/// <param name="FrameA">For <see cref="AlignmentEventKind.Frames"/>, the frame in document A.</param>
/// <param name="FrameB">For <see cref="AlignmentEventKind.Frames"/>, the frame in document B.</param>
public readonly record struct AlignmentEvent(AlignmentEventKind Kind, int Match, FrameKey FrameA, FrameKey FrameB)
{
    /// <summary>"Session ready": the ServerGreeting of each document.</summary>
    public static AlignmentEvent SessionReady => new(AlignmentEventKind.SessionReady, -1, default, default);

    /// <summary>The first frame of a union entity in each document (a given invocation, say).</summary>
    /// <param name="match">Index in <see cref="CompareSession.Matches"/>; the entity should be in both.</param>
    public static AlignmentEvent OnEntity(int match) => new(AlignmentEventKind.Entity, match, default, default);

    /// <summary>A frame picked in each document.</summary>
    public static AlignmentEvent OnFrames(FrameKey a, FrameKey b) => new(AlignmentEventKind.Frames, -1, a, b);
}

/// <summary>How one document's Stopwatch ticks map to compare time.</summary>
/// <param name="Pin">The tick that maps to <paramref name="Base"/>.</param>
/// <param name="Frequency">The document's Stopwatch frequency.</param>
/// <param name="Base">Compare time (before the shift to zero) of <paramref name="Pin"/>.</param>
public readonly record struct SideClock(long Pin, long Frequency, long Base);

/// <summary>
/// The shared timeline of a compare. Compare time is in <see cref="TimeSpan"/> ticks (100 ns) so that documents
/// recorded at different Stopwatch frequencies share one unit; 0 is the earlier of the two aligned session starts, so
/// a ruler starts at 0. A <see cref="TimeWindow"/> over compare time uses <see cref="TimeSpan.TicksPerSecond"/> as its
/// frequency.
/// </summary>
/// <param name="Mode">The alignment.</param>
/// <param name="A">Document A's mapping.</param>
/// <param name="B">Document B's mapping.</param>
/// <param name="Zero">Subtracted from every mapped time so the earlier start is 0.</param>
/// <param name="AnchorTime">
/// Compare time of the instant both documents are pinned to: 0 for Relative (both starts), the alignment event for On
/// event (the ruler counts from it, "Δ from session ready"), A's start for Wall-clock.
/// </param>
/// <param name="OffsetA">Compare time of A's session start (shown in its file header).</param>
/// <param name="OffsetB">Compare time of B's session start: how far the alignment moved B.</param>
/// <param name="EventResolved">
/// For <see cref="Alignment.OnEvent"/>, false when the event was missing from a document and its start was used
/// instead; true otherwise.
/// </param>
public readonly record struct CompareClock(Alignment Mode, SideClock A, SideClock B, long Zero, long AnchorTime, long OffsetA, long OffsetB,
    bool EventResolved)
{
    /// <summary>Ticks per second of compare time.</summary>
    public const long Frequency = TimeSpan.TicksPerSecond;

    /// <summary>One document's mapping.</summary>
    public SideClock Get(CompareSide side) => side == CompareSide.A ? A : B;

    /// <summary>Compare time of a document's Stopwatch tick.</summary>
    public long ToCompare(CompareSide side, long ticks)
    {
        var clock = Get(side);
        return ToTimeSpanTicks(ticks - clock.Pin, clock.Frequency) + clock.Base - Zero;
    }

    /// <summary>A document's Stopwatch tick at a compare time (to open a region in Inspect).</summary>
    public long ToTicks(CompareSide side, long compareTime)
    {
        var clock = Get(side);
        return clock.Pin + FromTimeSpanTicks(compareTime + Zero - clock.Base, clock.Frequency);
    }

    /// <summary>Compare time of a document's session start.</summary>
    public long Offset(CompareSide side) => side == CompareSide.A ? OffsetA : OffsetB;

    /// <summary>Converts Stopwatch ticks at a frequency to <see cref="TimeSpan"/> ticks, without overflow.</summary>
    public static long ToTimeSpanTicks(long ticks, long frequency)
        => frequency == TimeSpan.TicksPerSecond ? ticks : (long)((Int128)ticks * TimeSpan.TicksPerSecond / frequency);

    /// <summary>Converts <see cref="TimeSpan"/> ticks to Stopwatch ticks at a frequency, without overflow.</summary>
    public static long FromTimeSpanTicks(long ticks, long frequency)
        => frequency == TimeSpan.TicksPerSecond ? ticks : (long)((Int128)ticks * frequency / TimeSpan.TicksPerSecond);

    /// <summary>Builds the clock of an alignment.</summary>
    /// <param name="mode">The alignment.</param>
    /// <param name="headerA">Document A's header (frequency, wall-clock anchor).</param>
    /// <param name="startA">Document A's session start (its own t0), in its Stopwatch ticks.</param>
    /// <param name="eventA">For On event, A's event tick; null when A lacks it (its start is used).</param>
    /// <param name="headerB">Document B's header.</param>
    /// <param name="startB">Document B's session start.</param>
    /// <param name="eventB">For On event, B's event tick; null when B lacks it.</param>
    public static CompareClock Create(Alignment mode, in NntlFileHeader headerA, long startA, long? eventA, in NntlFileHeader headerB,
        long startB, long? eventB)
    {
        var frequencyA = Math.Max(1, headerA.StopwatchFrequency);
        var frequencyB = Math.Max(1, headerB.StopwatchFrequency);
        SideClock a, b;
        var resolved = true;
        switch (mode)
        {
            case Alignment.WallClock:
                // Each tick maps to its UTC time; both documents then share the real clock.
                a = new SideClock(headerA.StopwatchTicksAtAnchor, frequencyA, headerA.WallClockUtcTicks);
                b = new SideClock(headerB.StopwatchTicksAtAnchor, frequencyB, headerB.WallClockUtcTicks);
                break;
            case Alignment.OnEvent:
                resolved = eventA.HasValue && eventB.HasValue;
                a = new SideClock(eventA ?? startA, frequencyA, 0);
                b = new SideClock(eventB ?? startB, frequencyB, 0);
                break;
            default:
                a = new SideClock(startA, frequencyA, 0);
                b = new SideClock(startB, frequencyB, 0);
                break;
        }

        var mappedA = ToTimeSpanTicks(startA - a.Pin, frequencyA) + a.Base;
        var mappedB = ToTimeSpanTicks(startB - b.Pin, frequencyB) + b.Base;
        var zero = Math.Min(mappedA, mappedB);
        var anchor = mode switch
        {
            Alignment.OnEvent => -zero,
            Alignment.WallClock => mappedA - zero,
            _ => 0,
        };
        return new CompareClock(mode, a, b, zero, anchor, mappedA - zero, mappedB - zero, resolved);
    }
}
