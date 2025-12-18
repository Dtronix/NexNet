using System.Collections.Generic;
using NexNet.Internals;

namespace NexNet.Invocation;

/// <summary>
/// Manager for a session's groups.
/// </summary>
public class GroupManager
{
    private readonly INexusSession _session;
    private readonly IGroupRegistry _groupRegistry;

    internal GroupManager(INexusSession session, IGroupRegistry groupRegistry)
    {
        _session = session;
        _groupRegistry = groupRegistry;
    }

    /// <summary>
    /// Adds the current session to a group.  Used for grouping invocations.
    /// </summary>
    /// <param name="groupName">Group to add this session to.</param>
    public void Add(string groupName)
    {
        // Note: For local implementation this is synchronous.
        // Phase 5 will make this properly async.
        _ = _groupRegistry.AddToGroupAsync(groupName, _session);
    }


    /// <summary>
    /// Adds the current session to multiple groups.  Used for grouping invocations.
    /// </summary>
    /// <param name="groupNames">Groups to add this session to.</param>
    public void Add(string[] groupNames)
    {
        // Note: For local implementation this is synchronous.
        // Phase 5 will make this properly async.
        _ = _groupRegistry.AddToGroupsAsync(groupNames, _session);
    }

    /// <summary>
    /// Removes the current session from a group.  Used for grouping invocations.
    /// </summary>
    /// <param name="groupName">Group to remove this session from.</param>
    public void Remove(string groupName)
    {
        // Note: For local implementation this is synchronous.
        // Phase 5 will make this properly async.
        _ = _groupRegistry.RemoveFromGroupAsync(groupName, _session);
    }

    /// <summary>
    /// Gets all the connected client ids.
    /// </summary>
    /// <returns>Collection of connected client ids.</returns>
    public IEnumerable<string> GetNames()
    {
        // Note: For local implementation this is synchronous.
        // Phase 5 will make this properly async.
        var task = _groupRegistry.GetGroupNamesAsync();
        return task.IsCompleted ? task.Result : task.AsTask().GetAwaiter().GetResult();
    }
}
