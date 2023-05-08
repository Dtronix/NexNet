using System;

namespace NexNet.Transports;

/// <summary>
/// Default reconnection policy retries to connect at 0, 2, 10 &amp; 30 seconds.  Continues to try to reconnect every 30 seconds.
/// </summary>
public class DefaultReconnectionPolicy : IReconnectionPolicy
{
    /// <summary>
    /// Timespans used for the reconnection times.
    /// </summary>
    public TimeSpan[] TimeSpans { get; }

    /// <summary>
    /// If set to true the last <see cref="TimeSpans"/> will be used to repeat.
    /// </summary>
    public bool ContinuousRetry { get; }

    /// <summary>
    /// Creates a reconnection in default mode.
    /// </summary>
    public DefaultReconnectionPolicy()
        : this(null)
    {

    }

    /// <summary>
    /// Creates a reconnection policy with the specified timespans.
    /// </summary>
    /// <param name="timeSpans">Timespans to use for reconnection delays.</param>
    /// <param name="continuousRetry">If set to true the last <see cref="TimeSpans"/> will be used to repeat.</param>
    public DefaultReconnectionPolicy(TimeSpan[]? timeSpans, bool continuousRetry = true)
    {
        timeSpans ??= new[]
        {
            TimeSpan.FromSeconds(0),
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(30)
        };

        TimeSpans = timeSpans;
        ContinuousRetry = continuousRetry;
    }

    /// <inheritdoc />
    public TimeSpan? ReconnectDelay(int retryAttempt)
    {
        if (retryAttempt < TimeSpans.Length)
            return TimeSpans[retryAttempt];

        if (ContinuousRetry)
        {
            return TimeSpans[^1];
        }

        return null;
    }
}
