namespace NexNet.Pools;

/// <summary>
/// Generic object pool for types with parameterless constructors.
/// </summary>
/// <typeparam name="T">The type of objects to pool.</typeparam>
internal class ObjectPool<T> : PoolBase<T> where T : class, new()
{
    /// <summary>
    /// Creates a new object pool with the specified maximum size.
    /// </summary>
    /// <param name="maxSize">Maximum items to retain.</param>
    public ObjectPool(int maxSize = 128) : base(maxSize)
    {
    }

    /// <inheritdoc />
    protected override T Create() => new T();
}

/// <summary>
/// Static object pool for shared/global pooling scenarios.
/// </summary>
/// <typeparam name="T">The type of objects to pool.</typeparam>
internal static class StaticObjectPool<T> where T : class, new()
{
    private static readonly ObjectPool<T> _instance = new(128);

    /// <summary>
    /// Maximum items to retain in the pool.
    /// </summary>
    public static int MaxPoolSize => _instance.MaxSize;

    /// <summary>
    /// Gets the current pool count (for diagnostics).
    /// </summary>
    public static int PoolCount => _instance.CurrentCount;

    /// <summary>
    /// Rents an item from the pool.
    /// </summary>
    public static T Rent() => _instance.Rent();

    /// <summary>
    /// Returns an item to the pool.
    /// </summary>
    public static void Return(T? item) => _instance.Return(item);

    /// <summary>
    /// Clears all pooled items.
    /// </summary>
    public static void Clear() => _instance.Clear();
}
