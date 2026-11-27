using NexNet.Inspector.Core.Format;

namespace NexNet.Inspector.Core.Assembly;

/// <summary>
/// A <see cref="TransportOpStore"/> over one direction of a recorded session: it keeps only each op's block offset and
/// reads op bytes back from the recording on demand, holding the most recently used ones in a cache bounded by
/// <see cref="CacheCapacity"/>. A large recording is never loaded whole.
/// </summary>
/// <remarks>
/// Ops must be appended with bytes they own (as <see cref="SharedNntlReader"/> returns them): the newest ones enter
/// the cache as they are, so the assembler reading a frame right after its op was indexed does not touch the file.
/// Not thread-safe; see <see cref="TransportOpStore"/>.
/// </remarks>
public sealed class RecordingOpStore : TransportOpStore
{
    /// <summary>The default cache bound, per direction.</summary>
    public const long DefaultCacheCapacity = 16L * 1024 * 1024;

    private readonly SharedNntlReader _reader;
    private readonly Dictionary<int, LinkedListNode<CacheEntry>> _cache = [];
    private readonly LinkedList<CacheEntry> _lru = new();
    private long _cached;

    /// <summary>Creates an empty store.</summary>
    /// <param name="reader">The recording.</param>
    /// <param name="cacheCapacity">Bound on cached op bytes.</param>
    public RecordingOpStore(SharedNntlReader reader, long cacheCapacity = DefaultCacheCapacity)
        : base(0)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentOutOfRangeException.ThrowIfLessThan(cacheCapacity, 1);
        _reader = reader;
        CacheCapacity = cacheCapacity;
    }

    /// <summary>Bound on cached op bytes.</summary>
    public long CacheCapacity { get; }

    /// <summary>Op bytes currently cached.</summary>
    public long CachedBytes => _cached;

    /// <summary>Ops read back from the recording because they were not cached.</summary>
    public long CacheMisses { get; private set; }

    /// <inheritdoc/>
    protected override void OnAppended(int index, ReadOnlyMemory<byte> bytes) => Remember(index, bytes);

    /// <inheritdoc/>
    protected override bool TryGetOpBytes(int index, in StreamOp op, out ReadOnlyMemory<byte> bytes)
    {
        if (_cache.TryGetValue(index, out var node))
        {
            _lru.Remove(node);
            _lru.AddFirst(node);
            bytes = node.Value.Bytes;
            return true;
        }

        CacheMisses++;
        if (!_reader.TryReadTransportOp(op.Source, out var read) || read.Bytes.Length != op.Length)
        {
            bytes = default;
            return false;
        }

        bytes = read.Bytes;
        Remember(index, bytes);
        return true;
    }

    private void Remember(int index, ReadOnlyMemory<byte> bytes)
    {
        if (_cache.ContainsKey(index))
            return;

        // An op larger than the whole cache is handed out but not kept.
        if (bytes.Length > CacheCapacity)
            return;

        LinkedListNode<CacheEntry>? reuse = null;
        while (_cached + bytes.Length > CacheCapacity && _lru.Last is { } last)
        {
            _lru.RemoveLast();
            _cache.Remove(last.Value.Index);
            _cached -= last.Value.Bytes.Length;
            reuse = last;
        }

        // Evicted nodes are reused, so a steady scrub allocates no list nodes.
        var entry = new CacheEntry(index, bytes);
        if (reuse is null)
        {
            reuse = new LinkedListNode<CacheEntry>(entry);
        }
        else
        {
            reuse.Value = entry;
        }

        _lru.AddFirst(reuse);
        _cache[index] = reuse;
        _cached += bytes.Length;
    }

    private readonly record struct CacheEntry(int Index, ReadOnlyMemory<byte> Bytes);
}
