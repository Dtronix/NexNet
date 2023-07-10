using System;

namespace NexNet.Internals.Tasks;

internal interface IManualResetAwaiterSource
{
    /// <summary>
    /// Attempts to transition the exception state.
    /// </summary>
    /// <returns>True on successful setting of an exception state; False otherwise.</returns>
    bool TrySetException(Exception exception);

    /// <summary>
    /// Attempts to transition to the canceled state.
    /// </summary>
    /// <returns>True on successful setting of the state; False otherwise</returns>
    bool TrySetCanceled();

    /// <summary>
    /// Reset the awaiter to an unset state.
    /// </summary>
    void Reset();
}
