using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using NexNet.Internals;

namespace NexNet.Invocation;

internal class SessionManager
{
    public readonly ConcurrentDictionary<long, INexNetSession> Sessions = new();

    private readonly ConcurrentDictionary<int, SessionGroup> _sessionGroups = new();
    private static int _idCounter = 0;

    private ConcurrentDictionary<string, int> _groupIdDictionary = new();

    public bool RegisterSession(INexNetSession session)
    {
        return Sessions.TryAdd(session.Id, session);
    }

    public void UnregisterSession(INexNetSession session)
    {
        if (!Sessions.TryRemove(session.Id, out var _))
            return;
        var groups = session.RegisteredGroups;
        lock (groups)
        {
            var count = groups.Count;
            for (var i = 0; i < count; i++)
            {
                if (!_sessionGroups.TryGetValue(groups[i], out var group))
                    continue;

                group.UnregisterSession(session);
            }

            groups.Clear();
            groups.TrimExcess();
        }
    }

    public void UnregisterSessionGroup(string groupName, INexNetSession session)
    {

        if (!_groupIdDictionary.TryGetValue(groupName, out int id))
            return;

        if (!_sessionGroups.TryGetValue(id, out var group))
            return;

        group.UnregisterSession(session);
    }


    public void RegisterSessionGroup(string groupName, INexNetSession session)
    {
        var id = _groupIdDictionary.GetOrAdd(groupName, name => Interlocked.Increment(ref _idCounter));
        // ReSharper disable once InconsistentlySynchronizedField
        var group = _sessionGroups.GetOrAdd(id, name => new SessionGroup());
        group.RegisterSession(session);

        var groups = session.RegisteredGroups;
        lock (groups)
        {
            groups.Add(id);
        }
    }

    public void RegisterSessionGroup(string[] groupNames, INexNetSession session)
    {
        int[] groupIds = new int[groupNames.Length];

        // Create or get group ids for all the groups.
        for (int i = 0; i < groupNames.Length; i++)
        {
            groupIds[i] = _groupIdDictionary.GetOrAdd(groupNames[i], name => Interlocked.Increment(ref _idCounter));
            // ReSharper disable once InconsistentlySynchronizedField
            var group = _sessionGroups.GetOrAdd(groupIds[i], name => new SessionGroup());
            group.RegisterSession(session);
        }

        var groups = session.RegisteredGroups;
        lock (groups)
        {
            groups.AddRange(groupIds);
        }
    }

    public async ValueTask GroupChannelIterator<T>(string groupName, Func<INexNetSession, T, ValueTask> channelIterator, T state)
    {
        if (!_groupIdDictionary.TryGetValue(groupName, out int id))
            return;

        if (!_sessionGroups.TryGetValue(id, out var group))
            return;

        foreach (var nexNetSession in group.SessionDictionary)
        {
            await channelIterator(nexNetSession.Value, state).ConfigureAwait(false);
        }
    }

    private class SessionGroup
    {
        public readonly ConcurrentDictionary<long, INexNetSession> SessionDictionary = new();

        public bool RegisterSession(INexNetSession session)
        {
            return SessionDictionary.TryAdd(session.Id, session);
        }

        public void UnregisterSession(INexNetSession session)
        {
            if (!SessionDictionary.TryRemove(session.Id, out var _))
                return;
        }
    }
}
