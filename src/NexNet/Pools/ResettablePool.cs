namespace NexNet.Pools;

/// <summary>
/// Object pool for resettable types. Calls Reset() on return.
/// </summary>
/// <typeparam name="T">The type of objects to pool (must implement IResettable).</typeparam>
internal class ResettablePool<T> : PoolBase<T> where T : class, IResettable, new()
{
    /// <summary>
    /// Creates a new resettable pool with the specified maximum size.
    /// </summary>
    /// <param name="maxSize">Maximum items to retain.</param>
    public ResettablePool(int maxSize = 128) : base(maxSize)
    {
    }

    /// <inheritdoc />
    protected override T Create() => new T();

    /// <inheritdoc />
    protected override void OnReturn(T item) => item.Reset();
}
