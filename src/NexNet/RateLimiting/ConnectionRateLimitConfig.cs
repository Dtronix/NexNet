using System;
using System.Collections.Generic;
using System.Net;

namespace NexNet.RateLimiting;

/// <summary>
/// Configuration for connection rate limiting.
/// Default values provide basic DoS protection (1000 concurrent connections, 100/second).
/// Set values to 0 to disable specific limits.
/// </summary>
public class ConnectionRateLimitConfig
{
    private int _maxConcurrentConnections = 1000;
    private int _globalConnectionsPerSecond = 100;
    private int _maxConnectionsPerIp = 0;
    private int _connectionsPerIpPerWindow = 0;
    private int _perIpWindowSeconds = 60;
    private int _banDurationSeconds = 300;
    private int _banThreshold = 5;
    private HashSet<string>? _whitelistedIps;

    /// <summary>
    /// Maximum total concurrent connections allowed on this server.
    /// 0 = unlimited (disabled). Must be between 0 and 100,000.
    /// </summary>
    public int MaxConcurrentConnections
    {
        get => _maxConcurrentConnections;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 100_000);
            _maxConcurrentConnections = value;
        }
    }

    /// <summary>
    /// Maximum new connections per second globally.
    /// 0 = unlimited (disabled). Must be between 0 and 10,000.
    /// </summary>
    public int GlobalConnectionsPerSecond
    {
        get => _globalConnectionsPerSecond;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 10_000);
            _globalConnectionsPerSecond = value;
        }
    }

    /// <summary>
    /// Maximum concurrent connections from a single IP address.
    /// 0 = unlimited (disabled). Must be between 0 and 10,000.
    /// </summary>
    public int MaxConnectionsPerIp
    {
        get => _maxConnectionsPerIp;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 10_000);
            _maxConnectionsPerIp = value;
        }
    }

    /// <summary>
    /// Maximum new connections from a single IP within the time window.
    /// 0 = unlimited (disabled). Must be between 0 and 10,000.
    /// </summary>
    public int ConnectionsPerIpPerWindow
    {
        get => _connectionsPerIpPerWindow;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 10_000);
            _connectionsPerIpPerWindow = value;
        }
    }

    /// <summary>
    /// Time window in seconds for per-IP rate limiting.
    /// Must be between 1 and 3600 (1 hour). Default: 60 seconds.
    /// </summary>
    public int PerIpWindowSeconds
    {
        get => _perIpWindowSeconds;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 3600);
            _perIpWindowSeconds = value;
        }
    }

    /// <summary>
    /// Duration in seconds to temporarily ban an IP after repeated violations.
    /// 0 = no banning. Must be between 0 and 86400 (24 hours). Default: 300 (5 minutes).
    /// </summary>
    public int BanDurationSeconds
    {
        get => _banDurationSeconds;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 86_400);
            _banDurationSeconds = value;
        }
    }

    /// <summary>
    /// Number of rate limit violations before an IP is temporarily banned.
    /// Must be between 1 and 100. Default: 5.
    /// </summary>
    public int BanThreshold
    {
        get => _banThreshold;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 100);
            _banThreshold = value;
        }
    }

    /// <summary>
    /// Set of IP addresses that bypass all rate limiting.
    /// Whitelisted IPs are not subject to any limits and cannot be banned.
    /// IP addresses are normalized (IPv6 canonical form) for comparison.
    /// </summary>
    /// <remarks>
    /// Use this for trusted infrastructure like load balancers, monitoring systems,
    /// or internal services that need unrestricted access.
    /// </remarks>
    public HashSet<string>? WhitelistedIps
    {
        get => _whitelistedIps;
        set => _whitelistedIps = value != null ? NormalizeIpSet(value) : null;
    }

    /// <summary>
    /// Returns true if any rate limiting is enabled.
    /// </summary>
    public bool IsEnabled =>
        MaxConcurrentConnections > 0 ||
        GlobalConnectionsPerSecond > 0 ||
        MaxConnectionsPerIp > 0 ||
        ConnectionsPerIpPerWindow > 0;

    /// <summary>
    /// Checks if the given IP address is whitelisted.
    /// </summary>
    /// <param name="ipAddress">The IP address to check (will be normalized).</param>
    /// <returns>True if the IP is whitelisted, false otherwise.</returns>
    public bool IsWhitelisted(string? ipAddress)
    {
        if (_whitelistedIps == null || _whitelistedIps.Count == 0)
            return false;

        if (string.IsNullOrWhiteSpace(ipAddress))
            return false;

        // Normalize for comparison
        if (IPAddress.TryParse(ipAddress, out var parsed))
        {
            return _whitelistedIps.Contains(parsed.ToString());
        }

        return false;
    }

    /// <summary>
    /// Normalizes a set of IP addresses to their canonical forms.
    /// </summary>
    private static HashSet<string> NormalizeIpSet(HashSet<string> ips)
    {
        var normalized = new HashSet<string>(StringComparer.Ordinal);
        foreach (var ip in ips)
        {
            if (IPAddress.TryParse(ip, out var parsed))
            {
                normalized.Add(parsed.ToString());
            }
            else
            {
                // Keep non-IP strings as-is (allows for future extension)
                normalized.Add(ip);
            }
        }
        return normalized;
    }
}
