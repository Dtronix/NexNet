using System.Collections.Generic;
using NexNet.Internals;

namespace NexNet.Invocation;

/// <summary>
/// Manager for a session's groups.
/// </summary>
public class GroupManager
{
    private readonly INexusSession _session;
    private readonly SessionManager _sessionManager;

    internal GroupManager(INexusSession session, SessionManager sessionManager)
    {
        _session = session;
        _sessionManager = sessionManager;
    }

    /// <summary>
    /// Adds the current session to a group.  Used for grouping invocations.
    /// </summary>
    /// <param name="groupName">Group to add this session to.</param>
    public void Add(string groupName)
    {
        _sessionManager.RegisterSessionGroup(groupName, _session);
    }


    /// <summary>
    /// Adds the current session to multiple groups.  Used for grouping invocations.
    /// </summary>
    /// <param name="groupNames">Groups to add this session to.</param>
    public void Add(string[] groupNames)
    {
        _sessionManager.RegisterSessionGroup(groupNames, _session);
    }

    /// <summary>
    /// Removes the current session from a group.  Used for grouping invocations.
    /// </summary>
    /// <param name="groupName">Group to remove this session from.</param>
    public void Remove(string groupName)
    {
        _sessionManager.UnregisterSessionGroup(groupName, _session);
    }

    /// <summary>
    /// Gets all the connected client ids.
    /// </summary>
    /// <returns>Collection of connected client ids.</returns>
    public IEnumerable<string> GetNames()
    {
        return _sessionManager.Groups.Keys;
    }
}
