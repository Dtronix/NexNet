namespace NexNet.RateLimiting;

/// <summary>
/// Statistics for connection rate limiting monitoring.
/// </summary>
public readonly struct ConnectionRateLimitStats
{
    /// <summary>Current total concurrent connections.</summary>
    public int CurrentConnections { get; init; }

    /// <summary>Total connections accepted since server start.</summary>
    public long TotalAccepted { get; init; }

    /// <summary>Total connections rejected since server start.</summary>
    public long TotalRejected { get; init; }

    /// <summary>Number of unique IPs currently banned.</summary>
    public int BannedIpCount { get; init; }

    /// <summary>Number of unique IPs with active connections.</summary>
    public int UniqueIpCount { get; init; }
}
