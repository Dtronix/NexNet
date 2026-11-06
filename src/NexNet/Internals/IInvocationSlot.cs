namespace NexNet.Internals;

/// <summary>
/// An invocation's execution slot (one of <c>MaxConcurrentConnectionInvocations</c>, or of
/// <c>MaxSeparatePoolInvocations</c> for a separate-pool method).
/// </summary>
internal interface IInvocationSlot
{
    /// <summary>
    /// Frees the slot. Only the first call has an effect.
    /// </summary>
    void Release();
}
