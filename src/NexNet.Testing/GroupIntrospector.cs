using NexNet.Invocation;

namespace NexNet.Testing;

/// <summary>
/// Indexer-style accessor returned by
/// <see cref="NexusTestHost{TServerNexus, TClientProxy, TClientNexus, TServerProxy}.Groups"/>;
/// lookups by name produce a <see cref="GroupView"/> backed by the live server-side group
/// registry, so subsequent reads reflect the current membership.
/// </summary>
public sealed class GroupIntrospector
{
    private readonly IGroupRegistry _registry;

    internal GroupIntrospector(IGroupRegistry registry)
    {
        _registry = registry;
    }

    /// <summary>Returns a view onto the named group; group names need not exist yet.</summary>
    public GroupView this[string groupName] => new(groupName, _registry);
}
