using System.Collections.Concurrent;
using System.Collections.Generic;
using NexNet.Internals;

namespace NexNet.Invocation;

/// <summary>
/// A group of sessions.
/// </summary>
internal sealed class LocalSessionGroup
{
    private readonly ConcurrentDictionary<long, INexusSession> _sessionDictionary = new();

    public int Count => _sessionDictionary.Count;
    public IEnumerable<INexusSession> Sessions => _sessionDictionary.Values;

    public bool RegisterSession(INexusSession session)
    {
        return _sessionDictionary.TryAdd(session.Id, session);
    }

    public void UnregisterSession(INexusSession session)
    {
        _sessionDictionary.TryRemove(session.Id, out _);
    }
}
