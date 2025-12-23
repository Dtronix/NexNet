using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Threading;

namespace NexNet.RateLimiting;

/// <summary>
/// Default implementation of connection rate limiting.
/// Thread-safe for use with concurrent connection attempts.
/// </summary>
internal sealed class ConnectionRateLimiter : IConnectionRateLimiter
{
    private readonly ConnectionRateLimitConfig _config;

    // Global state
    private int _currentConnectionCount;
    private long _totalAccepted;
    private long _totalRejected;

    // Global rate limiting (sliding window)
    private readonly object _globalRateLock = new();
    private readonly Queue<long> _globalConnectionTimes = new();

    // Per-IP state (keyed by normalized IP string)
    private readonly ConcurrentDictionary<string, IpConnectionState> _ipStates = new();

    // Banned IPs with expiration timestamp (keyed by normalized IP string)
    private readonly ConcurrentDictionary<string, long> _bannedIps = new();

    // Cleanup timer for stale entries
    private readonly Timer _cleanupTimer;
    private const int CleanupIntervalMs = 60_000;

    public ConnectionRateLimiter(ConnectionRateLimitConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _cleanupTimer = new Timer(Cleanup, null, CleanupIntervalMs, CleanupIntervalMs);
    }

    public ConnectionRateLimitResult TryAcquire(string? remoteAddress)
    {
        var now = Environment.TickCount64;

        // Normalize the address for consistent lookup
        var normalizedAddress = NormalizeAddress(remoteAddress);

        // 0. Check if IP is whitelisted (bypass all rate limiting)
        if (_config.IsWhitelisted(remoteAddress))
        {
            // Still track connection count for stats, but don't enforce limits
            Interlocked.Increment(ref _currentConnectionCount);
            Interlocked.Increment(ref _totalAccepted);
            return ConnectionRateLimitResult.Allowed;
        }

        // 1. Check if IP is banned
        if (normalizedAddress != null && _config.BanDurationSeconds > 0)
        {
            if (_bannedIps.TryGetValue(normalizedAddress, out var banExpiry) && now < banExpiry)
            {
                Interlocked.Increment(ref _totalRejected);
                return ConnectionRateLimitResult.IpBanned;
            }
        }

        // 2. Check global concurrent connection limit (atomic increment-then-check)
        if (_config.MaxConcurrentConnections > 0)
        {
            var newCount = Interlocked.Increment(ref _currentConnectionCount);
            if (newCount > _config.MaxConcurrentConnections)
            {
                // Over limit - decrement and reject
                Interlocked.Decrement(ref _currentConnectionCount);
                RecordViolation(normalizedAddress, now);
                Interlocked.Increment(ref _totalRejected);
                return ConnectionRateLimitResult.MaxConcurrentConnectionsExceeded;
            }
        }

        // 3. Check global rate limit (sliding window)
        if (_config.GlobalConnectionsPerSecond > 0)
        {
            lock (_globalRateLock)
            {
                var windowStart = now - 1000; // 1 second window
                while (_globalConnectionTimes.Count > 0 && _globalConnectionTimes.Peek() < windowStart)
                    _globalConnectionTimes.Dequeue();

                if (_globalConnectionTimes.Count >= _config.GlobalConnectionsPerSecond)
                {
                    // Release the global slot we acquired earlier
                    if (_config.MaxConcurrentConnections > 0)
                    {
                        Interlocked.Decrement(ref _currentConnectionCount);
                    }
                    RecordViolation(normalizedAddress, now);
                    Interlocked.Increment(ref _totalRejected);
                    return ConnectionRateLimitResult.GlobalRateExceeded;
                }
            }
        }

        // 4. Per-IP checks (skip for UDS/null addresses)
        if (normalizedAddress != null)
        {
            var ipState = _ipStates.GetOrAdd(normalizedAddress, _ => new IpConnectionState());

            // 4a. Per-IP concurrent limit (atomic increment-then-check)
            if (_config.MaxConnectionsPerIp > 0)
            {
                var newIpCount = Interlocked.Increment(ref ipState.ConcurrentCount);
                if (newIpCount > _config.MaxConnectionsPerIp)
                {
                    // Over limit - decrement and reject
                    Interlocked.Decrement(ref ipState.ConcurrentCount);
                    // Also release the global slot we acquired earlier
                    if (_config.MaxConcurrentConnections > 0)
                    {
                        Interlocked.Decrement(ref _currentConnectionCount);
                    }
                    RecordViolation(normalizedAddress, now);
                    Interlocked.Increment(ref _totalRejected);
                    return ConnectionRateLimitResult.PerIpConcurrentLimitExceeded;
                }
            }

            // 4b. Per-IP rate limit (sliding window)
            if (_config.ConnectionsPerIpPerWindow > 0)
            {
                lock (ipState.Lock)
                {
                    var windowMs = _config.PerIpWindowSeconds * 1000L;
                    var windowStart = now - windowMs;

                    while (ipState.ConnectionTimes.Count > 0 &&
                           ipState.ConnectionTimes.Peek() < windowStart)
                        ipState.ConnectionTimes.Dequeue();

                    if (ipState.ConnectionTimes.Count >= _config.ConnectionsPerIpPerWindow)
                    {
                        // Release the per-IP slot we acquired earlier
                        if (_config.MaxConnectionsPerIp > 0)
                        {
                            Interlocked.Decrement(ref ipState.ConcurrentCount);
                        }
                        // Release the global slot we acquired earlier
                        if (_config.MaxConcurrentConnections > 0)
                        {
                            Interlocked.Decrement(ref _currentConnectionCount);
                        }
                        RecordViolation(normalizedAddress, now);
                        Interlocked.Increment(ref _totalRejected);
                        return ConnectionRateLimitResult.PerIpRateExceeded;
                    }
                }
            }

            // Acquire per-IP slot (only if not already acquired by concurrent limit check)
            if (_config.MaxConnectionsPerIp == 0)
            {
                Interlocked.Increment(ref ipState.ConcurrentCount);
            }

            if (_config.ConnectionsPerIpPerWindow > 0)
            {
                lock (ipState.Lock)
                {
                    ipState.ConnectionTimes.Enqueue(now);
                }
            }
        }

        // Acquire global slot (only if not already acquired by concurrent limit check)
        if (_config.MaxConcurrentConnections == 0)
        {
            Interlocked.Increment(ref _currentConnectionCount);
        }

        if (_config.GlobalConnectionsPerSecond > 0)
        {
            lock (_globalRateLock)
            {
                _globalConnectionTimes.Enqueue(now);
            }
        }

        Interlocked.Increment(ref _totalAccepted);
        return ConnectionRateLimitResult.Allowed;
    }

    public void Release(string? remoteAddress)
    {
        Interlocked.Decrement(ref _currentConnectionCount);

        var normalizedAddress = NormalizeAddress(remoteAddress);
        if (normalizedAddress != null && _ipStates.TryGetValue(normalizedAddress, out var ipState))
        {
            Interlocked.Decrement(ref ipState.ConcurrentCount);
            // Note: Cleanup timer will remove stale IP states
        }
    }

    public ConnectionRateLimitStats GetStats()
    {
        return new ConnectionRateLimitStats
        {
            CurrentConnections = Volatile.Read(ref _currentConnectionCount),
            TotalAccepted = Volatile.Read(ref _totalAccepted),
            TotalRejected = Volatile.Read(ref _totalRejected),
            BannedIpCount = _bannedIps.Count,
            UniqueIpCount = _ipStates.Count
        };
    }

    /// <summary>
    /// Normalizes a remote address string for consistent dictionary lookup.
    /// Attempts to parse as IP address for canonical form (handles IPv6 variations).
    /// Returns null for non-IP addresses (e.g., UDS paths) or null input.
    /// </summary>
    private static string? NormalizeAddress(string? remoteAddress)
    {
        if (string.IsNullOrWhiteSpace(remoteAddress))
            return null;

        // Try to parse as IP address to get canonical form
        // This handles IPv6 format variations (e.g., "::1" vs "0:0:0:0:0:0:0:1")
        if (IPAddress.TryParse(remoteAddress, out var ipAddress))
        {
            return ipAddress.ToString();
        }

        // Not a valid IP address (e.g., UDS path) - skip per-IP rate limiting
        return null;
    }

    private void RecordViolation(string? normalizedAddress, long now)
    {
        if (normalizedAddress == null || _config.BanDurationSeconds <= 0)
            return;

        var ipState = _ipStates.GetOrAdd(normalizedAddress, _ => new IpConnectionState());
        var violations = Interlocked.Increment(ref ipState.ViolationCount);

        if (violations >= _config.BanThreshold)
        {
            var banExpiry = now + (_config.BanDurationSeconds * 1000L);
            _bannedIps[normalizedAddress] = banExpiry;
            Interlocked.Exchange(ref ipState.ViolationCount, 0);
        }
    }

    private void Cleanup(object? state)
    {
        var now = Environment.TickCount64;

        // Clean up expired bans
        foreach (var kvp in _bannedIps)
        {
            if (now >= kvp.Value)
                _bannedIps.TryRemove(kvp.Key, out _);
        }

        // Clean up stale IP states
        var windowMs = Math.Max(_config.PerIpWindowSeconds * 1000L, 60_000L);
        var windowStart = now - windowMs;

        foreach (var kvp in _ipStates)
        {
            var ipState = kvp.Value;
            if (ipState.ConcurrentCount <= 0)
            {
                lock (ipState.Lock)
                {
                    while (ipState.ConnectionTimes.Count > 0 &&
                           ipState.ConnectionTimes.Peek() < windowStart)
                        ipState.ConnectionTimes.Dequeue();

                    if (ipState.ConcurrentCount <= 0 &&
                        ipState.ConnectionTimes.Count == 0 &&
                        ipState.ViolationCount == 0)
                    {
                        _ipStates.TryRemove(kvp.Key, out _);
                    }
                }
            }
        }
    }

    public void Dispose()
    {
        _cleanupTimer.Dispose();
        _ipStates.Clear();
        _bannedIps.Clear();
    }

    private sealed class IpConnectionState
    {
        public int ConcurrentCount;
        public int ViolationCount;
        public readonly object Lock = new();
        public readonly Queue<long> ConnectionTimes = new();
    }
}
