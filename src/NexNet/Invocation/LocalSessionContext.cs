using System.Collections.Concurrent;
using System.Threading;
using NexNet.Internals;

namespace NexNet.Invocation;

/// <summary>
/// Shared state for local session manager implementations.
/// </summary>
internal sealed class LocalSessionContext
{
    public readonly ConcurrentDictionary<long, INexusSession> Sessions = new();
    public readonly ConcurrentDictionary<string, int> GroupIdDictionary = new();
    public readonly ConcurrentDictionary<int, LocalSessionGroup> SessionGroups = new();

    private int _groupIdCounter = 0;

    public int GetNextGroupId() => Interlocked.Increment(ref _groupIdCounter);

    public void Clear()
    {
        Sessions.Clear();
        SessionGroups.Clear();
        GroupIdDictionary.Clear();
    }
}
