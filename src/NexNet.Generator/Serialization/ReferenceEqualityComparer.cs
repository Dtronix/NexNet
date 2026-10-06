using System.Runtime.CompilerServices;

namespace NexNet.Generator.Serialization;

/// <summary>
/// Compares by reference (netstandard2.0 has no <c>System.Collections.Generic.ReferenceEqualityComparer</c>).
/// </summary>
internal sealed class ReferenceEqualityComparer<T> : IEqualityComparer<T>
    where T : class
{
    public static readonly ReferenceEqualityComparer<T> Instance = new();

    private ReferenceEqualityComparer() { }

    public bool Equals(T? x, T? y) => ReferenceEquals(x, y);

    public int GetHashCode(T obj) => RuntimeHelpers.GetHashCode(obj);
}
