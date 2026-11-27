namespace NexNet.Inspector.Core.Assembly;

/// <summary>
/// A <see cref="TransportOpStore"/> for a live capture: op bytes are copied into large shared slabs as they arrive
/// (the attach stream reuses its buffer for the next block), so holding many small ops costs no array per op. The
/// oldest ops are dropped once more than <see cref="MaxRetainedBytes"/> are held; the viewer keeps only data since
/// attach, and not without bound.
/// </summary>
/// <remarks>Not thread-safe; see <see cref="TransportOpStore"/>.</remarks>
public sealed class LiveOpStore : TransportOpStore
{
    /// <summary>The default bound on retained op bytes per direction.</summary>
    public const long DefaultMaxRetainedBytes = 512L * 1024 * 1024;

    private const int SlabSize = 256 * 1024;

    // An op larger than this gets an array of its own rather than wasting the rest of a slab.
    private const int SharedLimit = SlabSize / 8;

    private ReadOnlyMemory<byte>[] _bytes = new ReadOnlyMemory<byte>[64];
    private byte[] _slab = [];
    private int _slabUsed;

    /// <summary>Creates an empty store.</summary>
    /// <param name="maxRetainedBytes">Bound on retained op bytes; the oldest ops are dropped past it.</param>
    /// <param name="startOffset">Absolute offset of the first byte that will be appended.</param>
    public LiveOpStore(long maxRetainedBytes = DefaultMaxRetainedBytes, long startOffset = 0)
        : base(startOffset)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxRetainedBytes, 1);
        MaxRetainedBytes = maxRetainedBytes;
    }

    /// <summary>Bound on retained op bytes.</summary>
    public long MaxRetainedBytes { get; }

    /// <summary>Bytes dropped from the front to stay under <see cref="MaxRetainedBytes"/>.</summary>
    public long TrimmedBytes { get; private set; }

    /// <inheritdoc/>
    protected override void OnAppended(int index, ReadOnlyMemory<byte> bytes)
    {
        var length = bytes.Length;
        Memory<byte> copy;
        if (length > SharedLimit)
        {
            copy = new byte[length];
        }
        else
        {
            if (_slab.Length - _slabUsed < length)
            {
                _slab = new byte[SlabSize];
                _slabUsed = 0;
            }

            copy = _slab.AsMemory(_slabUsed, length);
            _slabUsed += length;
        }

        bytes.Span.CopyTo(copy.Span);
        if (index >= _bytes.Length)
            Array.Resize(ref _bytes, Math.Max(_bytes.Length * 2, index + 1));
        _bytes[index] = copy;

        if (ByteCount > MaxRetainedBytes)
            TrimmedBytes += TrimOldest(MaxRetainedBytes);
    }

    /// <inheritdoc/>
    protected override bool TryGetOpBytes(int index, in StreamOp op, out ReadOnlyMemory<byte> bytes)
    {
        bytes = _bytes[index];
        return !bytes.IsEmpty;
    }

    /// <inheritdoc/>
    protected override void OnTrimmed(int index) => _bytes[index] = default;
}
