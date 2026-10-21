namespace NexNet.Diagnostics;

/// <summary>
/// Values summed over the sessions of one <see cref="MetricsTagSet"/> during a collection.
/// </summary>
internal struct SessionMetricsTotals
{
    public long Sessions;
    public long Handshaking;

    // Monotonic: also accumulated in MetricsTagSet.Retired when a session retires.
    public long FramesSent;
    public long BytesSent;
    public long FramesReceived;
    public long BytesReceived;

    public long BudgetReserved;
    public long BudgetLimit;
    public long PipesOpen;
    public long PipeWindow;
    public long InvocationsQueued;
    public long InvocationsExecuting;
    public long CreditWaits;
    public long LargeMessagesInbound;
    public long LargeMessagesOutbound;

    /// <summary>
    /// Adds the monotonic values of <paramref name="other"/>.
    /// </summary>
    public void AddMonotonic(in SessionMetricsTotals other)
    {
        FramesSent += other.FramesSent;
        BytesSent += other.BytesSent;
        FramesReceived += other.FramesReceived;
        BytesReceived += other.BytesReceived;
    }
}
