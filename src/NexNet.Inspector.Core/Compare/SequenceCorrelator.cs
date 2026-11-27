namespace NexNet.Inspector.Core.Compare;

/// <summary>
/// Pairs two ordered sequences of content digests the way a text diff pairs lines: the common head and tail first,
/// then a longest common subsequence of the rest, and what lies between two common elements paired by position (the
/// same call with a changed payload). An element inserted in one run therefore stays alone instead of shifting every
/// pairing after it.
/// </summary>
internal static class SequenceCorrelator
{
    /// <summary>Pairs <paramref name="a"/> with <paramref name="b"/>.</summary>
    /// <param name="a">A's digests in order.</param>
    /// <param name="b">B's digests in order.</param>
    /// <param name="maxCells">
    /// The largest LCS table worked out; a bigger middle is paired by position (bounded time and memory).
    /// </param>
    /// <param name="pairs">Receives the pairs in merged order: (index in a or -1, index in b or -1).</param>
    public static void Correlate(ReadOnlySpan<ulong> a, ReadOnlySpan<ulong> b, int maxCells, List<(int A, int B)> pairs)
    {
        var head = 0;
        while (head < a.Length && head < b.Length && a[head] == b[head])
        {
            pairs.Add((head, head));
            head++;
        }

        var tail = 0;
        while (tail < a.Length - head && tail < b.Length - head && a[a.Length - 1 - tail] == b[b.Length - 1 - tail])
            tail++;

        var midA = a.Length - head - tail;
        var midB = b.Length - head - tail;
        if (midA > 0 && midB > 0 && (long)midA * midB <= maxCells)
            Lcs(a.Slice(head, midA), b.Slice(head, midB), head, pairs);
        else
            Positional(head, midA, head, midB, pairs);

        for (var i = 0; i < tail; i++)
            pairs.Add((a.Length - tail + i, b.Length - tail + i));
    }

    private static void Lcs(ReadOnlySpan<ulong> a, ReadOnlySpan<ulong> b, int offset, List<(int A, int B)> pairs)
    {
        // Suffix table: lengths[i, j] = LCS of a[i..] and b[j..], so the walk below runs forwards.
        var width = b.Length + 1;
        var lengths = new int[(a.Length + 1) * width];
        for (var i = a.Length - 1; i >= 0; i--)
        {
            for (var j = b.Length - 1; j >= 0; j--)
            {
                lengths[(i * width) + j] = a[i] == b[j]
                    ? lengths[((i + 1) * width) + j + 1] + 1
                    : Math.Max(lengths[((i + 1) * width) + j], lengths[(i * width) + j + 1]);
            }
        }

        int x = 0, y = 0, runA = 0, runB = 0;
        while (x < a.Length && y < b.Length)
        {
            if (a[x] == b[y] && lengths[(x * width) + y] == lengths[((x + 1) * width) + y + 1] + 1)
            {
                Positional(offset + runA, x - runA, offset + runB, y - runB, pairs);
                pairs.Add((offset + x, offset + y));
                x++;
                y++;
                runA = x;
                runB = y;
            }
            else if (lengths[((x + 1) * width) + y] >= lengths[(x * width) + y + 1])
                x++;
            else
                y++;
        }

        Positional(offset + runA, a.Length - runA, offset + runB, b.Length - runB, pairs);
    }

    // Unequal elements between two common ones: the same position is the same call with other content; the excess of
    // the longer side exists only there.
    private static void Positional(int startA, int countA, int startB, int countB, List<(int A, int B)> pairs)
    {
        var both = Math.Min(countA, countB);
        for (var i = 0; i < both; i++)
            pairs.Add((startA + i, startB + i));
        for (var i = both; i < countA; i++)
            pairs.Add((startA + i, -1));
        for (var i = both; i < countB; i++)
            pairs.Add((-1, startB + i));
    }
}
