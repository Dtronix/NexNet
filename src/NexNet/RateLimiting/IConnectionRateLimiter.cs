using System;

namespace NexNet.RateLimiting;

/// <summary>
/// Interface for connection rate limiting.
/// </summary>
public interface IConnectionRateLimiter : IDisposable
{
    /// <summary>
    /// Checks if a new connection should be allowed and acquires a slot if allowed.
    /// </summary>
    /// <param name="remoteAddress">
    /// Remote address string from ITransport.RemoteAddress.
    /// For IP-based transports, this is the IP address.
    /// For UDS, this may be null or the socket path.
    /// When behind a proxy with TrustProxyHeaders enabled, this is the original client IP.
    /// </param>
    /// <returns>Result indicating whether connection is allowed or reason for rejection.</returns>
    ConnectionRateLimitResult TryAcquire(string? remoteAddress);

    /// <summary>
    /// Releases a connection slot when a connection is closed.
    /// Must be called for every successful TryAcquire.
    /// </summary>
    /// <param name="remoteAddress">Remote address string (same value passed to TryAcquire).</param>
    void Release(string? remoteAddress);

    /// <summary>
    /// Gets current statistics for monitoring.
    /// </summary>
    ConnectionRateLimitStats GetStats();
}
