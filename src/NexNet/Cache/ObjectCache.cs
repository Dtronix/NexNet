using System;
using System.Collections.Concurrent;

namespace NexNet.Cache;

internal static class ObjectCache<T> where T : class, new()
{
    // The pool of available objects
    private static readonly ConcurrentBag<T> _pool = new();

    /// <summary>
    /// Rent an instance of T from the cache.  
    /// If none are available, a new one is constructed via its parameterless ctor.
    /// </summary>
    public static T Rent()
    {
        if (_pool.TryTake(out var item))
            return item;
        return new T();
    }

    /// <summary>
    /// Return an instance to the cache for reuse.
    /// </summary>
    public static void Return(T item)
    {
        if (item == null) 
            throw new ArgumentNullException(nameof(item));
        _pool.Add(item);
    }

    /// <summary>
    /// Clear out all pooled items (e.g. to release references).
    /// </summary>
    public static void Clear()
    {
        while (_pool.TryTake(out _)) { }
    }
}
