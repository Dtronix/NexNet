using NexNet.Internals.Collections.Lists;

namespace NexNet.IntegrationTests.Collections.Lists;

internal static class VersionedListTestingExtensions 
{
    /// <summary>
    /// Fills the <see cref="VersionedList{T}"/> with negative integers from -1 descending.
    /// </summary>
    /// <param name="list">The versioned list to fill.</param>
    /// <param name="count">The number of negative values to add.</param>
    /// <returns>The same <see cref="VersionedList{T}"/> instance with added values.</returns>
    public static VersionedList<int> FillNegative(this VersionedList<int> list, int count)
    {
        for (int i = 0; i < count; i++)
        {
            list.List.Add(-i - 1);
        }

        return list;
    }
}
