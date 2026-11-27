using NexNet.Inspector.Core.Model;

namespace NexNet.Inspector.App.Model;

/// <summary>What changed on a <see cref="ScopeStack"/>.</summary>
public enum ScopeChangeKind : byte
{
    /// <summary>A scope was pushed: it is the current one now.</summary>
    Pushed = 0,

    /// <summary>The current scope was popped (Back): the one under it, if any, is current again.</summary>
    Popped = 1,

    /// <summary>Every scope was cleared (✕).</summary>
    Cleared = 2,
}

/// <summary>A change of a <see cref="ScopeStack"/>.</summary>
/// <param name="Kind">Pushed, popped or cleared.</param>
/// <param name="Window">
/// The time window to show now: the pushed scope's for a push, the window the views showed before the popped (or first)
/// scope was pushed for a pop or a clear; empty when it is not known.
/// </param>
public readonly record struct ScopeChange(ScopeChangeKind Kind, TimeWindow Window);

/// <summary>
/// A document's nested scopes (D2): Filter to time range pushes, Back pops, ✕ clears. A pushed scope is cut to the
/// current one, so a nested scope never reaches outside its parent. Each entry remembers the window shown before it was
/// pushed, so Back returns to that view. Not persisted (bookmarks are, I44). A pure model: no UI types; UI thread only.
/// </summary>
public sealed class ScopeStack
{
    private readonly List<(TimeScope Scope, TimeWindow Before)> _entries = [];

    /// <summary>Raised after every push, pop and clear.</summary>
    public event Action<ScopeChange>? Changed;

    /// <summary>How many scopes are nested; 0 when the document is not scoped.</summary>
    public int Depth => _entries.Count;

    /// <summary>True when no scope is set.</summary>
    public bool IsEmpty => _entries.Count == 0;

    /// <summary>The active scope; null when none is set.</summary>
    public TimeScope? Current => _entries.Count == 0 ? null : _entries[^1].Scope;

    /// <summary>The scope at <paramref name="depth"/>, 0 = the outermost.</summary>
    public TimeScope this[int depth] => _entries[depth].Scope;

    /// <summary>
    /// Pushes <paramref name="scope"/>, cut to the current scope; refused when it covers no time there (it lies outside
    /// the current scope, or is empty).
    /// </summary>
    /// <param name="scope">The new scope.</param>
    /// <param name="before">The window shown now, which Back returns to; empty when not known.</param>
    /// <returns>False when refused.</returns>
    public bool Push(TimeScope scope, TimeWindow before = default)
    {
        if (Current is { } parent) scope = scope with { Start = Math.Max(scope.Start, parent.Start), End = Math.Min(scope.End, parent.End) };
        if (scope.IsEmpty) return false;
        _entries.Add((scope, before));
        Changed?.Invoke(new ScopeChange(ScopeChangeKind.Pushed, scope.Window));
        return true;
    }

    /// <summary>Back: pops the current scope.</summary>
    /// <returns>False when there was none.</returns>
    public bool Pop()
    {
        if (_entries.Count == 0) return false;
        TimeWindow before = _entries[^1].Before;
        _entries.RemoveAt(_entries.Count - 1);
        Changed?.Invoke(new ScopeChange(ScopeChangeKind.Popped, before));
        return true;
    }

    /// <summary>✕: clears every scope.</summary>
    /// <returns>False when there was none.</returns>
    public bool Clear()
    {
        if (_entries.Count == 0) return false;
        TimeWindow before = _entries[0].Before;
        _entries.Clear();
        Changed?.Invoke(new ScopeChange(ScopeChangeKind.Cleared, before));
        return true;
    }

    /// <summary>Replaces this stack's scopes with <paramref name="other"/>'s (a live tab that became its saved file).</summary>
    public void CopyFrom(ScopeStack other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (ReferenceEquals(other, this)) return;
        _entries.Clear();
        _entries.AddRange(other._entries);
    }
}
