using NexNet.Inspector.Core.Assembly;
using NexNet.Inspector.Core.Framing;
using NexNet.Inspector.Core.Session;

namespace NexNet.Inspector.App.Controls.Compare;

/// <summary>
/// One document's frames as sorted start ticks per row category, for the heat rows: a cell's frame count is two binary
/// searches, so a redraw at any zoom costs the number of cells, not the number of frames, and allocates nothing. Kept in
/// the document's own Stopwatch ticks, so a new alignment only moves the cell boundaries (CompareClock.ToTicks).
/// </summary>
public sealed class CompareDensity
{
    private const int Categories = (int)CompareCategory.Handshake + 1;

    private readonly long[][] _ticks;

    private CompareDensity(long[][] ticks) => _ticks = ticks;

    /// <summary>An empty density (no frames).</summary>
    public static CompareDensity Empty { get; } = new(Enumerable.Range(0, Categories).Select(static _ => Array.Empty<long>()).ToArray());

    /// <summary>Frames of a category.</summary>
    public int Total(CompareCategory category) => _ticks[(int)category].Length;

    /// <summary>Frames of a category that start in [<paramref name="from"/>, <paramref name="to"/>), in document ticks.</summary>
    public int Count(CompareCategory category, long from, long to)
    {
        if (to <= from) return 0;
        long[] ticks = _ticks[(int)category];
        return LowerBound(ticks, to) - LowerBound(ticks, from);
    }

    /// <summary>Reads a session's frames (under its gate) into per-category tick lists.</summary>
    public static CompareDensity Build(CaptureSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        List<long>[] lists = new List<long>[Categories];
        for (int i = 0; i < lists.Length; i++) lists[i] = [];
        lock (session.Gate)
        {
            MessageAssembler assembler = session.Assembler;
            foreach (FrameKey key in session.FrameOrder)
            {
                if (!session.TryGetFrame(key, out FrameRecord frame)) continue;
                lists[(int)CompareCategory.Transport].Add(frame.StartTicks);
                lists[(int)CategoryOf(assembler, assembler.GetLink(key.Direction, key.Seq))].Add(frame.StartTicks);
            }
        }

        long[][] ticks = new long[Categories][];
        for (int i = 0; i < ticks.Length; i++)
        {
            ticks[i] = lists[i].ToArray();
            // Capture order is time order per direction; the two directions interleave only roughly.
            Array.Sort(ticks[i]);
        }

        return new CompareDensity(ticks);
    }

    private static CompareCategory CategoryOf(MessageAssembler assembler, in FrameLink link) => link.Entity switch
    {
        EntityKind.Invocation => CompareCategory.Invocations,
        EntityKind.Pipe when (uint)link.EntityIndex < (uint)assembler.Pipes.Length && assembler.Pipes[link.EntityIndex].Collection >= 0
            => CompareCategory.Collections,
        EntityKind.Pipe => CompareCategory.Pipes,
        EntityKind.Greeting => CompareCategory.Handshake,
        _ => CompareCategory.Control,
    };

    private static int LowerBound(long[] ticks, long value)
    {
        int low = 0, high = ticks.Length;
        while (low < high)
        {
            int mid = (low + high) >>> 1;
            if (ticks[mid] < value) low = mid + 1;
            else high = mid;
        }

        return low;
    }
}
