using System;

namespace NexNet.Transports;

/// <summary>
/// Default reconnection policy retries to connect at 0, 2, 10 & 30 seconds.  Continues to try to reconnect every 30 seconds.
/// </summary>
public class DefaultReconnectionPolicy : IReconnectionPolicy
{
    public TimeSpan[] TimeSpans { get; }
    public bool ContinuousRetry { get; }

    public DefaultReconnectionPolicy()
        : this(null, true)
    {

    }

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
    public TimeSpan? ReconnectDelay(int retryCount)
    {
        if (retryCount < TimeSpans.Length)
            return TimeSpans[retryCount];

        if (ContinuousRetry)
        {
            return TimeSpans[^1];
        }

        return null;
    }
}
