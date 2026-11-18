using System;
using NexNet.RateLimiting;
using System.Threading.Tasks;

namespace NexNet.IntegrationTests.Security;
internal class ConnectionRateLimiterUnitTests
{
    [Test]
    public async Task TryAcquire_WithinGlobalLimit_ReturnsAllowed()
    {
        var config = new ConnectionRateLimitConfig { MaxConcurrentConnections = 10 };
        using var limiter = new ConnectionRateLimiter(config, TimeProvider.System);

        var result = limiter.TryAcquire("127.0.0.1");

        await Assert.That(result).IsEqualTo(ConnectionRateLimitResult.Allowed);
    }

    [Test]
    public async Task TryAcquire_ExceedsGlobalLimit_ReturnsExceeded()
    {
        var config = new ConnectionRateLimitConfig { MaxConcurrentConnections = 2 };
        using var limiter = new ConnectionRateLimiter(config, TimeProvider.System);

        limiter.TryAcquire("127.0.0.1");
        limiter.TryAcquire("127.0.0.1");
        var result = limiter.TryAcquire("127.0.0.1");

        await Assert.That(result).IsEqualTo(ConnectionRateLimitResult.MaxConcurrentConnectionsExceeded);
    }

    [Test]
    public async Task Release_DecrementsCount()
    {
        var config = new ConnectionRateLimitConfig { MaxConcurrentConnections = 1 };
        using var limiter = new ConnectionRateLimiter(config, TimeProvider.System);

        limiter.TryAcquire("127.0.0.1");
        limiter.Release("127.0.0.1");
        var result = limiter.TryAcquire("127.0.0.1");

        await Assert.That(result).IsEqualTo(ConnectionRateLimitResult.Allowed);
    }

    [Test]
    public async Task BanMechanism_BansAfterThreshold()
    {
        var config = new ConnectionRateLimitConfig
        {
            MaxConnectionsPerIp = 1,
            BanThreshold = 2,
            BanDurationSeconds = 60
        };
        using var limiter = new ConnectionRateLimiter(config, TimeProvider.System);
        var ip = "192.168.1.1";

        limiter.TryAcquire(ip); // Allowed
        limiter.TryAcquire(ip); // Violation 1
        limiter.TryAcquire(ip); // Violation 2 -> Banned

        limiter.Release(ip);
        var result = limiter.TryAcquire(ip);

        await Assert.That(result).IsEqualTo(ConnectionRateLimitResult.IpBanned);
    }

    [Test]
    public async Task NullAddress_SkipsPerIpChecks()
    {
        var config = new ConnectionRateLimitConfig
        {
            MaxConcurrentConnections = 100,
            MaxConnectionsPerIp = 1
        };
        using var limiter = new ConnectionRateLimiter(config, TimeProvider.System);

        // UDS connections have null or path address
        var result1 = limiter.TryAcquire(null);
        var result2 = limiter.TryAcquire(null);

        await Assert.That(result1).IsEqualTo(ConnectionRateLimitResult.Allowed);
        await Assert.That(result2).IsEqualTo(ConnectionRateLimitResult.Allowed);
    }

    [Test]
    public async Task UdsPath_SkipsPerIpChecks()
    {
        var config = new ConnectionRateLimitConfig
        {
            MaxConcurrentConnections = 100,
            MaxConnectionsPerIp = 1
        };
        using var limiter = new ConnectionRateLimiter(config, TimeProvider.System);

        // UDS socket paths are not valid IP addresses
        var result1 = limiter.TryAcquire("/var/run/app.sock");
        var result2 = limiter.TryAcquire("/var/run/app.sock");

        // Both allowed because non-IP addresses skip per-IP checks
        await Assert.That(result1).IsEqualTo(ConnectionRateLimitResult.Allowed);
        await Assert.That(result2).IsEqualTo(ConnectionRateLimitResult.Allowed);
    }

    [Test]
    public async Task IPv6Normalization_TreatsEquivalentAddressesAsSame()
    {
        var config = new ConnectionRateLimitConfig { MaxConnectionsPerIp = 1 };
        using var limiter = new ConnectionRateLimiter(config, TimeProvider.System);

        // These are the same IPv6 address in different formats
        limiter.TryAcquire("::1");
        var result = limiter.TryAcquire("0:0:0:0:0:0:0:1");

        await Assert.That(result).IsEqualTo(ConnectionRateLimitResult.PerIpConcurrentLimitExceeded);
    }

    [Test]
    public async Task GetStats_ReturnsCorrectValues()
    {
        var config = new ConnectionRateLimitConfig { MaxConcurrentConnections = 10 };
        using var limiter = new ConnectionRateLimiter(config, TimeProvider.System);

        limiter.TryAcquire("192.168.1.1");
        limiter.TryAcquire("192.168.1.2");

        var stats = limiter.GetStats();

        await Assert.That(stats.CurrentConnections).IsEqualTo(2);
        await Assert.That(stats.TotalAccepted).IsEqualTo(2);
        await Assert.That(stats.UniqueIpCount).IsEqualTo(2);
    }

    [Test]
    public async Task ConfigValidation_ThrowsOnInvalidValues()
    {
        var config = new ConnectionRateLimitConfig();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => config.MaxConcurrentConnections = -1);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => config.MaxConcurrentConnections = 100_001);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => config.PerIpWindowSeconds = 0);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => config.BanThreshold = 0);
    }

    [Test]
    public async Task IsEnabled_ReturnsFalseWhenAllZero()
    {
        var config = new ConnectionRateLimitConfig
        {
            // Explicitly disable all limits (defaults are now enabled)
            MaxConcurrentConnections = 0,
            GlobalConnectionsPerSecond = 0,
            MaxConnectionsPerIp = 0,
            ConnectionsPerIpPerWindow = 0
        };
        await Assert.That(config.IsEnabled).IsFalse();
    }

    [Test]
    public async Task IsEnabled_ReturnsTrueWithDefaults()
    {
        // Defaults now have rate limiting enabled
        var config = new ConnectionRateLimitConfig();
        await Assert.That(config.IsEnabled).IsTrue();
    }

    [Test]
    public async Task IsEnabled_ReturnsTrueWhenAnySet()
    {
        var config = new ConnectionRateLimitConfig { MaxConcurrentConnections = 1 };
        await Assert.That(config.IsEnabled).IsTrue();
    }

    [Test]
    public async Task WhitelistedIp_BypassesAllLimits()
    {
        var config = new ConnectionRateLimitConfig
        {
            MaxConnectionsPerIp = 1,
            MaxConcurrentConnections = 1,
            WhitelistedIps = new HashSet<string> { "10.0.0.1" }
        };
        using var limiter = new ConnectionRateLimiter(config, TimeProvider.System);

        // Whitelisted IP bypasses all limits
        var result1 = limiter.TryAcquire("10.0.0.1");
        var result2 = limiter.TryAcquire("10.0.0.1");
        var result3 = limiter.TryAcquire("10.0.0.1");

        await Assert.That(result1).IsEqualTo(ConnectionRateLimitResult.Allowed);
        await Assert.That(result2).IsEqualTo(ConnectionRateLimitResult.Allowed);
        await Assert.That(result3).IsEqualTo(ConnectionRateLimitResult.Allowed);
    }

    [Test]
    public async Task WhitelistedIp_CannotBeBanned()
    {
        var config = new ConnectionRateLimitConfig
        {
            MaxConnectionsPerIp = 1,
            BanThreshold = 1,
            BanDurationSeconds = 60,
            WhitelistedIps = new HashSet<string> { "10.0.0.1" }
        };
        using var limiter = new ConnectionRateLimiter(config, TimeProvider.System);

        // Non-whitelisted IP gets banned
        limiter.TryAcquire("192.168.1.1");
        limiter.TryAcquire("192.168.1.1"); // Violation -> banned
        limiter.Release("192.168.1.1");
        var bannedResult = limiter.TryAcquire("192.168.1.1");
        await Assert.That(bannedResult).IsEqualTo(ConnectionRateLimitResult.IpBanned);

        // Whitelisted IP still works
        var whitelistedResult = limiter.TryAcquire("10.0.0.1");
        await Assert.That(whitelistedResult).IsEqualTo(ConnectionRateLimitResult.Allowed);
    }

    [Test]
    public async Task WhitelistedIp_NormalizesIPv6()
    {
        var config = new ConnectionRateLimitConfig
        {
            MaxConnectionsPerIp = 1,
            WhitelistedIps = new HashSet<string> { "::1" }
        };
        using var limiter = new ConnectionRateLimiter(config, TimeProvider.System);

        // IPv6 variations should all be recognized as whitelisted
        var result1 = limiter.TryAcquire("::1");
        var result2 = limiter.TryAcquire("0:0:0:0:0:0:0:1");

        await Assert.That(result1).IsEqualTo(ConnectionRateLimitResult.Allowed);
        await Assert.That(result2).IsEqualTo(ConnectionRateLimitResult.Allowed);
    }

    [Test]
    public async Task WhitelistedIp_StillCountsForStats()
    {
        var config = new ConnectionRateLimitConfig
        {
            MaxConcurrentConnections = 10,
            WhitelistedIps = new HashSet<string> { "10.0.0.1" }
        };
        using var limiter = new ConnectionRateLimiter(config, TimeProvider.System);

        limiter.TryAcquire("10.0.0.1");
        limiter.TryAcquire("10.0.0.1");

        var stats = limiter.GetStats();
        await Assert.That(stats.CurrentConnections).IsEqualTo(2);
        await Assert.That(stats.TotalAccepted).IsEqualTo(2);
    }

    [Test]
    public async Task ManyUniqueIps_AllAllowed()
    {
        var config = new ConnectionRateLimitConfig
        {
            MaxConcurrentConnections = 0,
            GlobalConnectionsPerSecond = 0,
            MaxConnectionsPerIp = 5,
            ConnectionsPerIpPerWindow = 0
        };
        using var limiter = new ConnectionRateLimiter(config, TimeProvider.System);

        var uniqueIps = 1000;
        var results = new List<ConnectionRateLimitResult>();

        for (int i = 0; i < uniqueIps; i++)
        {
            var ip = $"10.{i / 256}.{i % 256}.1";
            results.Add(limiter.TryAcquire(ip));
        }

        await Assert.That(results.All(r => r == ConnectionRateLimitResult.Allowed)).IsTrue();

        var stats = limiter.GetStats();
        await Assert.That(stats.UniqueIpCount).IsEqualTo(uniqueIps);
    }

    [Test]
    public async Task MultipleBans_TracksCorrectly()
    {
        var config = new ConnectionRateLimitConfig
        {
            MaxConnectionsPerIp = 1,
            BanThreshold = 1,
            BanDurationSeconds = 3600
        };
        using var limiter = new ConnectionRateLimiter(config, TimeProvider.System);

        for (int i = 0; i < 100; i++)
        {
            var ip = $"192.168.{i / 256}.{i % 256}";
            limiter.TryAcquire(ip);
            limiter.TryAcquire(ip);
        }

        var stats = limiter.GetStats();
        await Assert.That(stats.BannedIpCount).IsGreaterThan(0);
    }

    [Test]
    public async Task PerIpWindow_FakeTimeProvider_ExpiredEntriesAllowNewConnections()
    {
        var fakeTime = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(DateTimeOffset.UtcNow);
        var config = new ConnectionRateLimitConfig
        {
            ConnectionsPerIpPerWindow = 2,
            PerIpWindowSeconds = 60
        };
        using var limiter = new ConnectionRateLimiter(config, fakeTime);

        await Assert.That(limiter.TryAcquire("10.0.0.1")).IsEqualTo(ConnectionRateLimitResult.Allowed);
        await Assert.That(limiter.TryAcquire("10.0.0.1")).IsEqualTo(ConnectionRateLimitResult.Allowed);
        await Assert.That(limiter.TryAcquire("10.0.0.1")).IsEqualTo(ConnectionRateLimitResult.PerIpRateExceeded);

        // Advance past the 60s sliding window.
        fakeTime.Advance(TimeSpan.FromSeconds(61));

        await Assert.That(limiter.TryAcquire("10.0.0.1")).IsEqualTo(ConnectionRateLimitResult.Allowed).Because("Once the sliding window expires, a new connection from the same IP must be allowed.");
    }

    [Test]
    public async Task BanExpiration_FakeTimeProvider_ExpiredBanReleasesIp()
    {
        var fakeTime = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(DateTimeOffset.UtcNow);
        var config = new ConnectionRateLimitConfig
        {
            MaxConnectionsPerIp = 1,
            BanThreshold = 1,
            BanDurationSeconds = 30
        };
        using var limiter = new ConnectionRateLimiter(config, fakeTime);

        await Assert.That(limiter.TryAcquire("10.0.0.5")).IsEqualTo(ConnectionRateLimitResult.Allowed);
        // Second concurrent connection exceeds per-IP — records a violation that should ban the IP.
        await Assert.That(limiter.TryAcquire("10.0.0.5")).IsEqualTo(ConnectionRateLimitResult.PerIpConcurrentLimitExceeded);
        await Assert.That(limiter.TryAcquire("10.0.0.5")).IsEqualTo(ConnectionRateLimitResult.IpBanned);

        // Advance past the 30s ban — IP should be unbanned.
        fakeTime.Advance(TimeSpan.FromSeconds(31));

        var afterBan = limiter.TryAcquire("10.0.0.5");
        await Assert.That(afterBan).IsNotEqualTo(ConnectionRateLimitResult.IpBanned).Because("Once BanDurationSeconds elapses, the IP must no longer be banned.");
    }

}