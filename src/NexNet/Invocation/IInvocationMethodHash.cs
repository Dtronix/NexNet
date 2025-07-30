using System.Collections.Frozen;
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
    /// This is a hash based upon the interface's method names, arguments and return values.
    /// Used to ensure the other connection is in sync with this connection's hub.
    /// </summary>
    public static int GetMethodHash<T>()
        where T : IInvocationMethodHash
    {
        return T.MethodHash;
    }
}
