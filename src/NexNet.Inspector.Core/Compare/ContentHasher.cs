using System.Buffers;
using System.Buffers.Binary;
using System.Numerics;

namespace NexNet.Inspector.Core.Compare;

/// <summary>
/// A streaming 64-bit digest whose value depends only on the bytes and the boundaries, not on how the bytes were split
/// into segments: an op store hands a payload back as a sequence cut wherever the transport ops were. Not
/// cryptographic; it only tells equal content from different content within one compare.
/// </summary>
internal struct ContentHasher
{
    private const ulong Prime1 = 0x9E3779B185EBCA87UL;
    private const ulong Prime2 = 0xC2B2AE3D27D4EB4FUL;
    private const ulong Seed = 0x27D4EB2F165667C5UL;

    private ulong _hash;
    private ulong _pending;
    private int _pendingBytes;
    private long _length;
    private bool _started;

    /// <summary>Appends bytes.</summary>
    public void Append(ReadOnlySpan<byte> data)
    {
        Start();
        _length += data.Length;
        while (_pendingBytes != 0 && !data.IsEmpty)
        {
            _pending |= (ulong)data[0] << (8 * _pendingBytes);
            data = data[1..];
            if (++_pendingBytes == 8)
            {
                Mix(_pending);
                _pending = 0;
                _pendingBytes = 0;
            }
        }

        while (data.Length >= 8)
        {
            Mix(BinaryPrimitives.ReadUInt64LittleEndian(data));
            data = data[8..];
        }

        foreach (var b in data)
            _pending |= (ulong)b << (8 * _pendingBytes++);
    }

    /// <summary>Appends every segment of a sequence.</summary>
    public void Append(in ReadOnlySequence<byte> data)
    {
        foreach (var segment in data)
            Append(segment.Span);
    }

    /// <summary>Ends one piece and mixes in a marker, so "ab" + "c" and "a" + "bc" differ and absent differs from empty.</summary>
    public void Boundary(ulong marker)
    {
        Start();
        Mix(_pending ^ ((ulong)_pendingBytes << 56));
        Mix((ulong)_length);
        Mix(marker);
        _pending = 0;
        _pendingBytes = 0;
        _length = 0;
    }

    /// <summary>The digest of everything appended.</summary>
    public ulong Finish()
    {
        Boundary(0);
        var h = _hash;
        h ^= h >> 33;
        h *= 0xFF51AFD7ED558CCDUL;
        h ^= h >> 33;
        h *= 0xC4CEB9FE1A85EC53UL;
        h ^= h >> 33;
        return h;
    }

    private void Start()
    {
        if (_started)
            return;
        _started = true;
        _hash = Seed;
    }

    private void Mix(ulong word) => _hash = BitOperations.RotateLeft((_hash ^ word) * Prime1, 31) * Prime2;
}
