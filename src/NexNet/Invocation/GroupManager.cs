using System.Collections.Generic;
using System.Threading.Tasks;
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
    /// Adds the current session to a group. Used for grouping invocations.
    /// </summary>
    /// <param name="groupName">Group to add this session to.</param>
    public ValueTask AddAsync(string groupName)
    {
        return _groupRegistry.AddToGroupAsync(groupName, _session);
    }

    /// <summary>
    /// Adds the current session to multiple groups. Used for grouping invocations.
    /// </summary>
    /// <param name="groupNames">Groups to add this session to.</param>
    public ValueTask AddAsync(string[] groupNames)
    {
        return _groupRegistry.AddToGroupsAsync(groupNames, _session);
    }

    /// <summary>
    /// Removes the current session from a group. Used for grouping invocations.
    /// </summary>
    /// <param name="groupName">Group to remove this session from.</param>
    public ValueTask RemoveAsync(string groupName)
    {
        return _groupRegistry.RemoveFromGroupAsync(groupName, _session);
    }

    /// <summary>
    /// Gets all group names that have at least one member.
    /// </summary>
    /// <returns>Collection of group names.</returns>
    public ValueTask<IReadOnlyCollection<string>> GetNamesAsync()
    {
        return _groupRegistry.GetGroupNamesAsync();
    }
}
