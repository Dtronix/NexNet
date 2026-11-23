namespace NexNet.Inspector.App.Controls.Timeline;

/// <summary>
/// Gantt packing of invocations (timeline-lane README, hybrid packing): each interval, in start order, takes the first
/// row whose last interval ended before it starts, so the rows are as few as a first-fit allows and an interval never
/// moves once placed while the ones before it keep their ends.
/// </summary>
public static class LanePacker
{
    /// <summary>Packs intervals sorted by start into rows.</summary>
    /// <param name="starts">Each interval's first tick, ascending.</param>
    /// <param name="ends">Each interval's last tick.</param>
    /// <param name="rows">Receives each interval's row.</param>
    /// <param name="rowEnds">Scratch at least as long as the intervals: each row's last end.</param>
    /// <returns>The number of rows used.</returns>
    public static int Pack(ReadOnlySpan<long> starts, ReadOnlySpan<long> ends, Span<int> rows, Span<long> rowEnds)
    {
        if (ends.Length != starts.Length || rows.Length < starts.Length || rowEnds.Length < starts.Length)
            throw new ArgumentException("every interval needs an end, a row and a scratch slot");

        int count = 0;
        for (int i = 0; i < starts.Length; i++)
        {
            long start = starts[i];
            int row = 0;
            while (row < count && rowEnds[row] >= start)
                row++;
            if (row == count)
                count++;
            rowEnds[row] = Math.Max(start, ends[i]);
            rows[i] = row;
        }

        return count;
    }
}
