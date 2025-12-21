namespace NexNet.Pools;

/// <summary>
/// Interface for objects that can be reset for reuse in a pool.
/// </summary>
internal interface IResettable
{
    /// <summary>
    /// Resets the object to its initial state for reuse.
    /// </summary>
    void Reset();
}
