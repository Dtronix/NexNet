namespace NexNet.Inspector.Core.Compare;

/// <summary>
/// The differences of a compare in timeline order: what the Δ strip marks and its count badge shows. Entries at the
/// same time come worst first.
/// </summary>
public sealed class DeltaList
{
    private readonly DeltaEntry[] _entries;
    private readonly int[] _byMatch;

    internal DeltaList(DeltaEntry[] entries, int matchCount)
    {
        Array.Sort(entries, Compare);
        _entries = entries;
        _byMatch = new int[matchCount];
        Array.Fill(_byMatch, -1);
        for (var i = 0; i < entries.Length; i++)
        {
            ref readonly var entry = ref entries[i];
            if (entry.Kind == DeltaKind.Entity)
                _byMatch[entry.Match] = i;
            if (entry.Severity > MaxSeverity)
                MaxSeverity = entry.Severity;
        }
    }

    /// <summary>The entries, by time.</summary>
    public ReadOnlySpan<DeltaEntry> Entries => _entries;

    /// <summary>Entries.</summary>
    public int Count => _entries.Length;

    /// <summary>The worst severity; None when nothing differs.</summary>
    public DeltaSeverity MaxSeverity { get; }

    /// <summary>Entries at least this severe (the badge under a severity filter).</summary>
    public int CountAtLeast(DeltaSeverity severity)
    {
        var count = 0;
        foreach (ref readonly var entry in _entries.AsSpan())
        {
            if (entry.Severity >= severity)
                count++;
        }

        return count;
    }

    /// <summary>The entry of a union entity; -1 when its sides do not differ.</summary>
    /// <param name="match">Index in <see cref="CompareSession.Matches"/>.</param>
    public int IndexOfMatch(int match) => (uint)match < (uint)_byMatch.Length ? _byMatch[match] : -1;

    /// <summary>The first entry at or after a compare time (next-difference navigation); <see cref="Count"/> when none.</summary>
    public int IndexAtOrAfter(long time)
    {
        int low = 0, high = _entries.Length;
        while (low < high)
        {
            var mid = (low + high) >>> 1;
            if (_entries[mid].Time < time)
                low = mid + 1;
            else
                high = mid;
        }

        return low;
    }

    /// <summary>The order of the list: time, then severity (worst first), then entities before gaps, then index.</summary>
    public static int Compare(DeltaEntry x, DeltaEntry y)
    {
        var c = x.Time.CompareTo(y.Time);
        if (c != 0)
            return c;
        c = y.Severity.CompareTo(x.Severity);
        if (c != 0)
            return c;
        c = x.Kind.CompareTo(y.Kind);
        if (c != 0)
            return c;
        c = x.Side.CompareTo(y.Side);
        return c != 0 ? c : (x.Match + x.Gap).CompareTo(y.Match + y.Gap);
    }
}
