using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NexNet.Internals;

namespace NexNet.Invocation;

/// <summary>
/// Registry for managing group membership across sessions.
/// </summary>
internal interface IGroupRegistry
{
    /// <summary>
    /// Adds a session to a group.
    /// </summary>
    /// <param name="groupName">Name of the group.</param>
    /// <param name="session">Session to add.</param>
    ValueTask AddToGroupAsync(string groupName, INexusSession session);

    /// <summary>
    /// Adds a session to multiple groups.
    /// </summary>
    /// <param name="groupNames">Names of the groups.</param>
    /// <param name="session">Session to add.</param>
    ValueTask AddToGroupsAsync(string[] groupNames, INexusSession session);

    /// <summary>
    /// Removes a session from a group.
    /// </summary>
    /// <param name="groupName">Name of the group.</param>
    /// <param name="session">Session to remove.</param>
    ValueTask RemoveFromGroupAsync(string groupName, INexusSession session);

    /// <summary>
    /// Removes a session from all groups it belongs to.
    /// Called during session disconnection.
    /// </summary>
    /// <param name="session">Session to remove from all groups.</param>
    ValueTask RemoveFromAllGroupsAsync(INexusSession session);

    /// <summary>
    /// Gets all group names that have at least one member.
    /// </summary>
    ValueTask<IReadOnlyCollection<string>> GetGroupNamesAsync();

    /// <summary>
    /// Gets the count of sessions in a group (local only for local impl).
    /// </summary>
    /// <param name="groupName">Name of the group.</param>
    ValueTask<long> GetGroupSizeAsync(string groupName);

    /// <summary>
    /// Gets sessions that are members of a group (local sessions only).
    /// </summary>
    /// <param name="groupName">Name of the group.</param>
    IEnumerable<INexusSession> GetLocalGroupMembers(string groupName);

    /// <summary>
    /// Initializes the registry. Called when the server starts.
    /// </summary>
    ValueTask InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Shuts down the registry. Called when the server stops.
    /// </summary>
    ValueTask ShutdownAsync(CancellationToken cancellationToken = default);
}
