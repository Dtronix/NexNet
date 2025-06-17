using System.Collections.Immutable;
using NexNet.Internals.Collections.Versioned;

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
        var builder = ImmutableList.CreateBuilder<int>();
        for (int i = 0; i < count; i++)
        {
            builder.Add(-i - 1);
        }
        
        list.State = new VersionedList<int>.ListState(builder.ToImmutable(), 0);

        return list;
    }
}
