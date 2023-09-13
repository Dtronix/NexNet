using System.Collections.Concurrent;
using System.Collections.Generic;

namespace NexNet.Cache;

internal static class ListPool<T>
{
    private static readonly ConcurrentBag<List<T>> _pool = new ConcurrentBag<List<T>>();

    public static IReadOnlyList<T> Empty { get; } = new List<T>(0);

    public static List<T> Rent() => _pool.TryTake(out var list) ? list : new List<T>();

    public static void Return(List<T> list)
    {
        list.Capacity = 0;
        list.Clear();
        _pool.Add(list);
    }

    public static void Clear()
    {
        _pool.Clear();
    }
}
