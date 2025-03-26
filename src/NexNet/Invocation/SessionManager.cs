using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NexNet.Internals;
using static System.Collections.Specialized.BitVector32;

namespace NexNet.Invocation;
/// <summary>
/// Manages all connected sessions to the server and their groupings.
/// </summary>
internal class SessionManager
{
    private readonly ConcurrentDictionary<int, SessionGroup> _sessionGroups = new();
    private static int _idCounter = 0;

    private readonly ConcurrentDictionary<string, int> _groupIdDictionary = new();


    public readonly ConcurrentDictionary<long, INexusSession> Sessions = new();

    public IReadOnlyDictionary<string, int> Groups => _groupIdDictionary;

    public bool RegisterSession(INexusSession session)
    {
        return Sessions.TryAdd(session.Id, session);
    }

    public void UnregisterSession(INexusSession session)
    {
        if (!Sessions.TryRemove(session.Id, out var _))
            return;
        var groups = session.RegisteredGroups;
        lock (session.RegisteredGroupsLock)
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

    public void UnregisterSessionGroup(string groupName, INexusSession session)
    {

        if (!_groupIdDictionary.TryGetValue(groupName, out int id))
            return;

        if (!_sessionGroups.TryGetValue(id, out var group))
            return;

        group.UnregisterSession(session);
    }


    public void RegisterSessionGroup(string groupName, INexusSession session)
    {
        var id = _groupIdDictionary.GetOrAdd(groupName, name => Interlocked.Increment(ref _idCounter));
        // ReSharper disable once InconsistentlySynchronizedField
        var group = _sessionGroups.GetOrAdd(id, name => new SessionGroup());
        group.RegisterSession(session);

        var groups = session.RegisteredGroups;
        lock (session.RegisteredGroupsLock)
        {
            groups.Add(id);
        }
    }

    public void RegisterSessionGroup(string[] groupNames, INexusSession session)
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
        lock (session.RegisteredGroupsLock)
        {
            groups.AddRange(groupIds);
        }
    }

    public async ValueTask GroupChannelIterator<T>(
        string groupName,
        Func<INexusSession, T, ValueTask> channelIterator,
        T state,
        long? excludeSessionId)
    {
        if (!_groupIdDictionary.TryGetValue(groupName, out int id))
            return;

        if (!_sessionGroups.TryGetValue(id, out var group))
            return;

        foreach (var nexNetSession in group.SessionDictionary)
        {
            if(nexNetSession.Key == excludeSessionId)
                continue;

            try
            {
                await channelIterator(nexNetSession.Value, state).ConfigureAwait(false);
            }
            catch
            {
                // We ignore exceptions here.
            }
        }
    }

    private class SessionGroup
    {
        public readonly ConcurrentDictionary<long, INexusSession> SessionDictionary = new();

        public bool RegisterSession(INexusSession session)
        {
            return SessionDictionary.TryAdd(session.Id, session);
        }

        public void UnregisterSession(INexusSession session)
        {
            if (!SessionDictionary.TryRemove(session.Id, out var _))
                return;
        }
    }
}
