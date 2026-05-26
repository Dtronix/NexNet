using System.Collections.Generic;
using System.Linq;
using NexNet.Invocation;

namespace NexNet.Testing;

/// <summary>
/// Read-only view onto a single named group on the host. Returned from
/// <see cref="NexusTestHost{TServerNexus, TClientProxy, TClientNexus, TServerProxy}.Groups"/>
/// indexer; snapshots membership at the time the property is read so tests can compare against
/// expected sets without races on iteration.
/// </summary>
public sealed class GroupView
{
    private readonly string _groupName;
    private readonly IGroupRegistry _registry;

    internal GroupView(string groupName, IGroupRegistry registry)
    {
        _groupName = groupName;
        _registry = registry;
    }

    /// <summary>The group name as registered on the server.</summary>
    public string Name => _groupName;

    /// <summary>
    /// Snapshot of the session ids currently in the group. Returns an empty array if the group
    /// has no members or has never been used.
    /// </summary>
    public long[] Members => _registry.GetLocalGroupMembers(_groupName).Select(s => s.Id).ToArray();

    /// <summary>The number of sessions currently in the group.</summary>
    public int Count => Members.Length;
}
