using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace NexNet.Invocation;

/// <summary>
/// Provides a store for passing information between session invocations.
/// </summary>
public class SessionStore : IDisposable
{
    private readonly ConcurrentDictionary<string, object> _store = new();

    private bool _isDisposed;
    /// <summary>
    /// Attempts to get the value associated with the specified key from the store.
    /// </summary>
    /// <param name="key">The key of the value to get.</param>
    /// <param name="value">
    /// When this method returns, <paramref name="value"/> contains the object from
    /// the store with the specified key or the default value of
    /// null, if the operation failed.
    /// </param>
    /// <returns>true if the key was found in the store; otherwise, false.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is a null reference.</exception>
    public bool TryGet(string key, out object? value) => _store.TryGetValue(key, out value);

    /// <summary>
    /// Attempts to add the specified key and value to the store.
    /// </summary>
    /// <param name="key">The key of the element to add.</param>
    /// <param name="value">The value of the element to add. The value can be a null reference for reference types.</param>
    /// <returns>
    /// true if the key/value pair was added to the store successfully; otherwise, false.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is null reference.</exception>
    /// <exception cref="OverflowException">The store contains too many elements.</exception>

    public bool TryAdd(string key, object value) => _store.TryAdd(key, value);


    /// <summary>Gets or sets the value associated with the specified key.</summary>
    /// <param name="key">The key of the value to get or set.</param>
    /// <value>
    /// The value associated with the specified key. If the specified key is not found, a get operation throws a
    /// <see cref="KeyNotFoundException"/>, and a set operation creates a new element with the specified key.
    /// </value>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="key"/> is a null reference (Nothing in Visual Basic).
    /// </exception>
    /// <exception cref="KeyNotFoundException">
    /// The property is retrieved and <paramref name="key"/> does not exist in the collection.
    /// </exception>
    public object this[string key]
    {
        get => _store[key];
        set => _store[key] = value;
    }

    void IDisposable.Dispose()
    {
        if (_isDisposed)
            return;

        _isDisposed = true;

        _store.Clear();
    }
}
