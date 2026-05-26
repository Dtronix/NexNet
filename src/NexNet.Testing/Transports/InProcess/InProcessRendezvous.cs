using System;
using System.Collections.Concurrent;

namespace NexNet.Testing.Transports.InProcess;

/// <summary>
/// Process-local registry mapping endpoint names to <see cref="InProcessTransportListener"/>
/// instances. Servers register themselves on listener creation; clients look up the registered
/// listener by endpoint to obtain a paired transport.
/// </summary>
/// <remarks>
/// The registry is per-AppDomain (static). Endpoint names are arbitrary strings; tests typically
/// use a unique GUID-derived value per test method so concurrent runs do not collide. Multiple
/// listeners cannot register under the same endpoint; the second attempt throws.
/// </remarks>
internal static class InProcessRendezvous
{
    private static readonly ConcurrentDictionary<string, InProcessTransportListener> _listeners = new();

    /// <summary>
    /// Registers a listener under the given endpoint name. Throws if the endpoint is already taken.
    /// </summary>
    public static void Register(string endpoint, InProcessTransportListener listener)
    {
        if (!_listeners.TryAdd(endpoint, listener))
            throw new InvalidOperationException(
                $"An InProcess listener is already registered at endpoint '{endpoint}'.");
    }

    /// <summary>
    /// Removes the listener registration for the given endpoint, if any. Safe to call on an
    /// already-unregistered endpoint. Removal is conditional: we only remove the entry when it
    /// still maps to <paramref name="listener"/>, so a different listener that re-registered
    /// under the same endpoint between Register and Unregister isn't blown away.
    /// </summary>
    public static void Unregister(string endpoint, InProcessTransportListener listener)
    {
        if (_listeners.TryGetValue(endpoint, out var current) && ReferenceEquals(current, listener))
            _listeners.TryRemove(endpoint, out _);
    }

    /// <summary>
    /// Looks up the listener registered under the given endpoint name. Returns null when no
    /// listener is registered.
    /// </summary>
    public static InProcessTransportListener? Find(string endpoint)
    {
        return _listeners.TryGetValue(endpoint, out var listener) ? listener : null;
    }
}
