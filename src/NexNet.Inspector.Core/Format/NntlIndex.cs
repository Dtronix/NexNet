namespace NexNet.Inspector.Core.Format;

/// <summary>Block offsets of a file grouped by block kind and session, in the order groups first appear.</summary>
/// <remarks>
/// The writer builds it while appending and stores it as the footer; a scan rebuilds the same groups in the same
/// order, so a rebuilt footer is byte-identical to the original. Header and schema blocks are grouped under session 0.
/// </remarks>
public sealed class NntlIndex
{
    private readonly List<NntlIndexGroup> _groups = [];
    private readonly Dictionary<(NntlBlockKind Kind, long SessionId), NntlIndexGroup> _byKey = [];
    private readonly List<long> _sessionIds = [];
    private readonly HashSet<long> _sessionSet = [];

    /// <summary>Groups in order of first appearance.</summary>
    public IReadOnlyList<NntlIndexGroup> Groups => _groups;

    /// <summary>Ids of every session with at least one block, in order of first appearance.</summary>
    public IReadOnlyList<long> SessionIds => _sessionIds;

    /// <summary>Number of indexed blocks.</summary>
    public long BlockCount { get; private set; }

    /// <summary>Returns the group of <paramref name="kind"/> blocks of a session, or null.</summary>
    public NntlIndexGroup? FindGroup(NntlBlockKind kind, long sessionId = 0) =>
        _byKey.GetValueOrDefault((kind, HasSession(kind) ? sessionId : 0));

    /// <summary>Returns the entries of <paramref name="kind"/> blocks of a session, in file order.</summary>
    public ReadOnlySpan<NntlIndexEntry> GetEntries(NntlBlockKind kind, long sessionId = 0) =>
        FindGroup(kind, sessionId) is { } group ? group.Entries : default;

    /// <summary>Lists every indexed block in file order.</summary>
    public NntlBlockRef[] ToFileOrder()
    {
        var result = new NntlBlockRef[BlockCount];
        var i = 0;
        foreach (var group in _groups)
        {
            foreach (var e in group.Entries)
                result[i++] = new NntlBlockRef(e.Offset, group.Kind, group.SessionId, e.Ticks);
        }

        Array.Sort(result, static (a, b) => a.Offset.CompareTo(b.Offset));
        return result;
    }

    internal void Add(NntlBlockKind kind, long sessionId, long offset, long ticks)
    {
        var hasSession = HasSession(kind);
        if (!hasSession)
            sessionId = 0;

        if (!_byKey.TryGetValue((kind, sessionId), out var group))
        {
            group = new NntlIndexGroup(kind, sessionId);
            _byKey.Add((kind, sessionId), group);
            _groups.Add(group);
        }

        if (hasSession && _sessionSet.Add(sessionId))
            _sessionIds.Add(sessionId);

        group.Add(new NntlIndexEntry(offset, ticks));
        BlockCount++;
    }

    /// <summary>Whether blocks of <paramref name="kind"/> belong to a session.</summary>
    public static bool HasSession(NntlBlockKind kind) => kind is NntlBlockKind.SessionOpen
        or NntlBlockKind.TransportOp
        or NntlBlockKind.Gap
        or NntlBlockKind.Snapshot
        or NntlBlockKind.SessionClose;
}

/// <summary>The indexed blocks of one kind and session.</summary>
public sealed class NntlIndexGroup
{
    private NntlIndexEntry[] _entries = new NntlIndexEntry[4];
    private int _count;

    internal NntlIndexGroup(NntlBlockKind kind, long sessionId)
    {
        Kind = kind;
        SessionId = sessionId;
    }

    /// <summary>Block kind of the group.</summary>
    public NntlBlockKind Kind { get; }

    /// <summary>Owning session; 0 for header and schema blocks.</summary>
    public long SessionId { get; }

    /// <summary>Entries in file order.</summary>
    public ReadOnlySpan<NntlIndexEntry> Entries => _entries.AsSpan(0, _count);

    /// <summary>True while every entry's ticks are at least the previous entry's, which lets tick-range lookups
    /// binary search. Inbound and outbound ops of a session can be queued out of tick order, so this is checked
    /// rather than assumed.</summary>
    public bool TicksAscending { get; private set; } = true;

    internal void Add(NntlIndexEntry entry)
    {
        if (_count == _entries.Length)
            Array.Resize(ref _entries, _entries.Length * 2);
        if (_count > 0 && entry.Ticks < _entries[_count - 1].Ticks)
            TicksAscending = false;
        _entries[_count++] = entry;
    }

    /// <summary>Returns the slice of entries that may hold ticks in [<paramref name="fromTicks"/>,
    /// <paramref name="toTicks"/>]. Exact when ticks ascend; otherwise every entry, which callers filter.</summary>
    internal (int Start, int End) GetCandidateRange(long fromTicks, long toTicks)
    {
        if (!TicksAscending)
            return (0, _count);

        var entries = Entries;
        return (LowerBound(entries, fromTicks), UpperBound(entries, toTicks));
    }

    private static int LowerBound(ReadOnlySpan<NntlIndexEntry> entries, long ticks)
    {
        int lo = 0, hi = entries.Length;
        while (lo < hi)
        {
            var mid = (lo + hi) >>> 1;
            if (entries[mid].Ticks < ticks)
                lo = mid + 1;
            else
                hi = mid;
        }

        return lo;
    }

    private static int UpperBound(ReadOnlySpan<NntlIndexEntry> entries, long ticks)
    {
        int lo = 0, hi = entries.Length;
        while (lo < hi)
        {
            var mid = (lo + hi) >>> 1;
            if (entries[mid].Ticks <= ticks)
                lo = mid + 1;
            else
                hi = mid;
        }

        return lo;
    }

    internal NntlIndexEntry this[int i] => _entries[i];
}
