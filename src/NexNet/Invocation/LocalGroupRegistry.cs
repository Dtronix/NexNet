using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NexNet.Internals;
using NexNet.Logging;

namespace NexNet.Invocation;

/// <summary>
/// Local (single-server) implementation of IGroupRegistry.
/// </summary>
internal sealed class LocalGroupRegistry : IGroupRegistry
{
    private readonly LocalSessionContext _context;

    public LocalGroupRegistry(LocalSessionContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    public ValueTask AddToGroupAsync(string groupName, INexusSession session)
    {
        session.Logger?.LogDebug($"Registering session {session.Id} to group {groupName}");

        var id = _context.GroupIdDictionary.GetOrAdd(groupName, _ => _context.GetNextGroupId());
        var group = _context.SessionGroups.GetOrAdd(id, _ => new LocalSessionGroup());
        group.RegisterSession(session);

        lock (session.RegisteredGroupsLock)
        {
            session.RegisteredGroups.Add(id);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask AddToGroupsAsync(string[] groupNames, INexusSession session)
    {
        session.Logger?.LogDebug($"Registering session {session.Id} to groups {string.Join(',', groupNames)}");

        int[] groupIds = new int[groupNames.Length];

        for (int i = 0; i < groupNames.Length; i++)
        {
            groupIds[i] = _context.GroupIdDictionary.GetOrAdd(groupNames[i], _ => _context.GetNextGroupId());
            var group = _context.SessionGroups.GetOrAdd(groupIds[i], _ => new LocalSessionGroup());
            group.RegisterSession(session);
        }

        lock (session.RegisteredGroupsLock)
        {
            session.RegisteredGroups.AddRange(groupIds);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask RemoveFromGroupAsync(string groupName, INexusSession session)
    {
        if (!_context.GroupIdDictionary.TryGetValue(groupName, out int id))
            return ValueTask.CompletedTask;

        if (!_context.SessionGroups.TryGetValue(id, out var group))
            return ValueTask.CompletedTask;

        session.Logger?.LogDebug($"Unregistering session {session.Id} from group {groupName}");
        group.UnregisterSession(session);

        return ValueTask.CompletedTask;
    }

    public ValueTask RemoveFromAllGroupsAsync(INexusSession session)
    {
        lock (session.RegisteredGroupsLock)
        {
            var groups = session.RegisteredGroups;
            for (var i = 0; i < groups.Count; i++)
            {
                if (_context.SessionGroups.TryGetValue(groups[i], out var group))
                {
                    group.UnregisterSession(session);
                }
            }
            groups.Clear();
            groups.TrimExcess();
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<IReadOnlyCollection<string>> GetGroupNamesAsync()
    {
        return ValueTask.FromResult<IReadOnlyCollection<string>>(_context.GroupIdDictionary.Keys.ToArray());
    }

    public ValueTask<long> GetGroupSizeAsync(string groupName)
    {
        if (!_context.GroupIdDictionary.TryGetValue(groupName, out int id))
            return ValueTask.FromResult(0L);

        if (!_context.SessionGroups.TryGetValue(id, out var group))
            return ValueTask.FromResult(0L);

        return ValueTask.FromResult((long)group.Count);
    }

    public IEnumerable<INexusSession> GetLocalGroupMembers(string groupName)
    {
        if (!_context.GroupIdDictionary.TryGetValue(groupName, out int id))
            return Enumerable.Empty<INexusSession>();

        if (!_context.SessionGroups.TryGetValue(id, out var group))
            return Enumerable.Empty<INexusSession>();

        return group.Sessions;
    }

    public ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        return ValueTask.CompletedTask;
    }

    public ValueTask ShutdownAsync(CancellationToken cancellationToken = default)
    {
        _context.SessionGroups.Clear();
        _context.GroupIdDictionary.Clear();
        return ValueTask.CompletedTask;
    }
}
