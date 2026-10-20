namespace NexNet.Internals;

/// <summary>
/// An invocation's execution slot (one of <c>MaxConcurrentConnectionInvocations</c>).
/// </summary>
internal interface IInvocationSlot
{
    /// <summary>
    /// Frees the slot. Only the first call has an effect.
    /// </summary>
    void Release();
}
