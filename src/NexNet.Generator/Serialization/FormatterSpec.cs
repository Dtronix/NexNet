namespace NexNet.Generator.Serialization;

/// <summary>
/// One formatter registration produced by a <see cref="SerializationBuilder"/>. Specs from every producer (nexuses,
/// local <c>[NexusObject]</c> types, assembly declarations) are merged into a single generated file, deduplicated
/// by <see cref="TypeKey"/> (registrations) and <see cref="ClassName"/> (formatter classes).
/// </summary>
/// <param name="TypeKey">Fully qualified name of the closed type being registered.</param>
/// <param name="ClassName">
/// Generated formatter class backing the registration, or empty when a built-in or user formatter is registered.
/// Generic <c>[NexusObject]</c> types share one class across their closed registrations.
/// </param>
/// <param name="Code">Source of the formatter class named by <see cref="ClassName"/>; empty when there is none.</param>
/// <param name="Registration">The <c>NexusFormatterRegistry.Register</c> statement.</param>
internal sealed record FormatterSpec(string TypeKey, string ClassName, string Code, string Registration);

/// <summary>
/// Immutable array with value equality, so incremental pipeline steps holding it compare by content.
/// </summary>
internal readonly struct EquatableArray<T> : IEquatable<EquatableArray<T>>, IEnumerable<T>
    where T : IEquatable<T>
{
    public static readonly EquatableArray<T> Empty = new(Array.Empty<T>());

    private readonly T[]? _items;

    public EquatableArray(T[] items)
    {
        _items = items;
    }

    public int Length => _items?.Length ?? 0;

    public T this[int index] => _items![index];

    public bool Equals(EquatableArray<T> other)
    {
        var a = _items ?? Array.Empty<T>();
        var b = other._items ?? Array.Empty<T>();
        if (a.Length != b.Length)
            return false;

        for (var i = 0; i < a.Length; i++)
        {
            if (!a[i].Equals(b[i]))
                return false;
        }

        return true;
    }

    public override bool Equals(object? obj) => obj is EquatableArray<T> other && Equals(other);

    public override int GetHashCode()
    {
        var hash = 17;
        if (_items != null)
        {
            foreach (var item in _items)
                hash = unchecked(hash * 31 + item.GetHashCode());
        }

        return hash;
    }

    public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)(_items ?? Array.Empty<T>())).GetEnumerator();

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}
