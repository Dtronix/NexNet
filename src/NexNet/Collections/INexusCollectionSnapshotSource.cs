using NexNet.Diagnostics;
using NexNet.Internals;

namespace NexNet.Collections;

/// <summary>
/// A collection that can describe itself for <see cref="NexusSessionSnapshot.Collections"/>.
/// </summary>
internal interface INexusCollectionSnapshotSource
{
    /// <summary>
    /// The collection's state as seen from <paramref name="session"/>. Reads without locks and adds no cost to the
    /// collection's changes.
    /// </summary>
    NexusCollectionSnapshot GetSnapshot(INexusSession session);
}
