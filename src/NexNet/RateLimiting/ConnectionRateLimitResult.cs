namespace NexNet.RateLimiting;

/// <summary>
/// Result of a connection rate limit check.
/// </summary>
public enum ConnectionRateLimitResult
{
    /// <summary>Connection is allowed.</summary>
    Allowed,

    /// <summary>Rejected: Maximum concurrent connections reached.</summary>
    MaxConcurrentConnectionsExceeded,

    /// <summary>Rejected: Global connection rate exceeded.</summary>
    GlobalRateExceeded,

    /// <summary>Rejected: Per-IP concurrent connection limit exceeded.</summary>
    PerIpConcurrentLimitExceeded,

    /// <summary>Rejected: Per-IP rate limit exceeded.</summary>
    PerIpRateExceeded,

    /// <summary>Rejected: IP is temporarily banned.</summary>
    IpBanned
}
