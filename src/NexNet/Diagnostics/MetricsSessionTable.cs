using System;

namespace NexNet.Diagnostics;

/// <summary>
/// The live sessions of one <see cref="NexusMetrics"/>: an array of slots with a free list, so adding a session
/// allocates nothing once the array has grown to the peak number of sessions. The array shrinks again when it is
/// mostly empty.
/// </summary>
/// <remarks>
/// Not thread-safe: the owner calls every member under its lock.
/// </remarks>
internal sealed class MetricsSessionTable
{
    private const int MinCapacity = 16;

    private IMeteredSession?[] _slots = new IMeteredSession?[MinCapacity];
    private int[] _free = new int[MinCapacity];
    private int _freeCount;

    // Slots below this index have been used; the rest have not.
    private int _highWater;

    /// <summary>
    /// Sessions in the table.
    /// </summary>
    public int Count { get; private set; }

    /// <summary>
    /// Length of the slot array.
    /// </summary>
    public int Capacity => _slots.Length;

    /// <summary>
    /// Adds <paramref name="session"/> and sets its <see cref="IMeteredSession.MetricsSlot"/>.
    /// </summary>
    public void Add(IMeteredSession session)
    {
        int slot;
        if (_freeCount > 0)
        {
            slot = _free[--_freeCount];
        }
        else
        {
            if (_highWater == _slots.Length)
                Resize(_slots.Length * 2);
            slot = _highWater++;
        }

        _slots[slot] = session;
        session.MetricsSlot = slot;
        Count++;
    }

    /// <summary>
    /// Removes <paramref name="session"/>. Returns false if it is not in the table.
    /// </summary>
    public bool Remove(IMeteredSession session)
    {
        var slot = session.MetricsSlot;
        if (slot < 0 || slot >= _highWater || !ReferenceEquals(_slots[slot], session))
            return false;

        _slots[slot] = null;
        session.MetricsSlot = -1;
        Count--;

        if (slot == _highWater - 1)
            _highWater--;
        else
            _free[_freeCount++] = slot;

        if (_slots.Length > MinCapacity && Count < _slots.Length / 4)
            Compact();

        return true;
    }

    /// <summary>
    /// Slots up to the highest one in use; unused slots are null.
    /// </summary>
    public ReadOnlySpan<IMeteredSession?> Slots => _slots.AsSpan(0, _highWater);

    private void Resize(int capacity)
    {
        Array.Resize(ref _slots, capacity);
        Array.Resize(ref _free, capacity);
    }

    /// <summary>
    /// Moves every session to the front and halves the array.
    /// </summary>
    private void Compact()
    {
        var next = 0;
        for (var i = 0; i < _highWater; i++)
        {
            var session = _slots[i];
            if (session == null)
                continue;

            _slots[i] = null;
            _slots[next] = session;
            session.MetricsSlot = next;
            next++;
        }

        _highWater = next;
        _freeCount = 0;
        Resize(Math.Max(MinCapacity, _slots.Length / 2));
    }
}
