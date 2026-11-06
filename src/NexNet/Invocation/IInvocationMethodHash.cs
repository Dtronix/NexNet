using System.Collections.Frozen;
using System.Linq;
using System.Runtime.CompilerServices;

namespace NexNet.Invocation;

/// <summary>
/// Interface for registering hashes of public methods.
/// </summary>
public interface IInvocationMethodHash
{
    /// <summary>
    /// This is a hash based upon the interface's method names, arguments and return values.
    /// Used to ensure the other connection is in sync with this connection's hub.
    /// </summary>
    public static abstract int MethodHash { get; }


    /// <summary>
    /// A lookup table mapping each version identifier to its associated <see cref="MethodHash"/> value.  
    /// This allows you to retrieve the correct method-hash for a given version.
    /// </summary>
    public static abstract FrozenDictionary<string, int> VersionHashTable { get; }

    /// <summary>
    /// A set containing all method-hash values across all known version+method combinations of the interface.
    /// Used to determine if certain invocations are allowed.
    /// </summary>
    public static abstract FrozenSet<long> VersionMethodHashSet { get; }

    /// <summary>
    /// True when the method with <paramref name="methodId"/> runs in the session's separate invocation pool
    /// (<c>[NexusMethod(SeparatePool = true)]</c>). Generated-code infrastructure: the source generator overrides it
    /// for a nexus with such methods. Must be O(1) and allocation-free; it is read for every received invocation.
    /// </summary>
    /// <param name="methodId">The id of the invoked method.</param>
    public static virtual bool IsSeparatePoolMethod(ushort methodId) => false;


    /// <summary>
    /// This is a hash based upon the interface's method names, arguments and return values.
    /// Used to ensure the other connection is in sync with this connection's hub.
    /// </summary>
    public static int GetMethodHash<T>()
        where T : IInvocationMethodHash
    {
        return T.MethodHash;
    }

    /// <summary>
    /// Gets the version hash table for the specified type implementing <see cref="IInvocationMethodHash"/>.
    /// </summary>
    /// <typeparam name="T">The type that implements <see cref="IInvocationMethodHash"/>.</typeparam>
    /// <returns>A frozen dictionary mapping version identifiers to their associated method hash values.</returns>
    public static FrozenDictionary<string, int> GetVersionHashTable<T>()
        where T : IInvocationMethodHash
    {
        return T.VersionHashTable;
    }

    /// <summary>
    /// Gets the latest version string identifier for the specified type implementing <see cref="IInvocationMethodHash"/>.
    /// </summary>
    /// <typeparam name="T">The type that implements <see cref="IInvocationMethodHash"/>.</typeparam>
    /// <returns>The first version string from the version hash table, or null if the table is empty.</returns>
    public static string? GetLatestVersionString<T>()
        where T : IInvocationMethodHash
    {
        return T.VersionHashTable.Count == 0 ? null : T.VersionHashTable.Keys.LastOrDefault();
    }

    /// <summary>
    /// Gets the latest version hash for the specified type implementing <see cref="IInvocationMethodHash"/>.
    /// </summary>
    /// <typeparam name="T">The type that implements <see cref="IInvocationMethodHash"/>.</typeparam>
    /// <returns>The first hash value from the version hash table, or the base method hash if the table is empty.</returns>
    public static int GetLatestVersionHash<T>()
        where T : IInvocationMethodHash
    {
        return T.VersionHashTable.Count == 0 ? T.MethodHash : T.VersionHashTable.Values.LastOrDefault();
    }

    /// <summary>
    /// Gets the version method hash set for the specified type implementing <see cref="IInvocationMethodHash"/>.
    /// </summary>
    /// <typeparam name="T">The type that implements <see cref="IInvocationMethodHash"/>.</typeparam>
    /// <returns>A frozen set containing all method hash values across all known version+method combinations.</returns>
    public static FrozenSet<long> GetVersionMethodHashSet<T>()
        where T : IInvocationMethodHash
    {
        return T.VersionMethodHashSet;
    }
}
