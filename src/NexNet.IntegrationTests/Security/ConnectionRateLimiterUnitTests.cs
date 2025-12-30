using NexNet.RateLimiting;
using NUnit.Framework;

namespace NexNet.IntegrationTests.Security;

[TestFixture]
internal class ConnectionRateLimiterUnitTests
{
    [Test]
    public void TryAcquire_WithinGlobalLimit_ReturnsAllowed()
    {
        var config = new ConnectionRateLimitConfig { MaxConcurrentConnections = 10 };
        using var limiter = new ConnectionRateLimiter(config);

        var result = limiter.TryAcquire("127.0.0.1");

        Assert.That(result, Is.EqualTo(ConnectionRateLimitResult.Allowed));
    }

    [Test]
    public void TryAcquire_ExceedsGlobalLimit_ReturnsExceeded()
    {
        var config = new ConnectionRateLimitConfig { MaxConcurrentConnections = 2 };
        using var limiter = new ConnectionRateLimiter(config);

        limiter.TryAcquire("127.0.0.1");
        limiter.TryAcquire("127.0.0.1");
        var result = limiter.TryAcquire("127.0.0.1");

        Assert.That(result, Is.EqualTo(ConnectionRateLimitResult.MaxConcurrentConnectionsExceeded));
    }

    [Test]
    public void Release_DecrementsCount()
    {
        var config = new ConnectionRateLimitConfig { MaxConcurrentConnections = 1 };
        using var limiter = new ConnectionRateLimiter(config);

        limiter.TryAcquire("127.0.0.1");
        limiter.Release("127.0.0.1");
        var result = limiter.TryAcquire("127.0.0.1");

        Assert.That(result, Is.EqualTo(ConnectionRateLimitResult.Allowed));
    }

    [Test]
    public void BanMechanism_BansAfterThreshold()
    {
        var config = new ConnectionRateLimitConfig
        {
            MaxConnectionsPerIp = 1,
            BanThreshold = 2,
            BanDurationSeconds = 60
        };
        using var limiter = new ConnectionRateLimiter(config);
        var ip = "192.168.1.1";

        limiter.TryAcquire(ip); // Allowed
        limiter.TryAcquire(ip); // Violation 1
        limiter.TryAcquire(ip); // Violation 2 -> Banned

        limiter.Release(ip);
        var result = limiter.TryAcquire(ip);

        Assert.That(result, Is.EqualTo(ConnectionRateLimitResult.IpBanned));
    }

    [Test]
    public void NullAddress_SkipsPerIpChecks()
    {
        var config = new ConnectionRateLimitConfig
        {
            MaxConcurrentConnections = 100,
            MaxConnectionsPerIp = 1
        };
        using var limiter = new ConnectionRateLimiter(config);

        // UDS connections have null or path address
        var result1 = limiter.TryAcquire(null);
        var result2 = limiter.TryAcquire(null);

        Assert.That(result1, Is.EqualTo(ConnectionRateLimitResult.Allowed));
        Assert.That(result2, Is.EqualTo(ConnectionRateLimitResult.Allowed));
    }

    [Test]
    public void UdsPath_SkipsPerIpChecks()
    {
        var config = new ConnectionRateLimitConfig
        {
            MaxConcurrentConnections = 100,
            MaxConnectionsPerIp = 1
        };
        using var limiter = new ConnectionRateLimiter(config);

        // UDS socket paths are not valid IP addresses
        var result1 = limiter.TryAcquire("/var/run/app.sock");
        var result2 = limiter.TryAcquire("/var/run/app.sock");

        // Both allowed because non-IP addresses skip per-IP checks
        Assert.That(result1, Is.EqualTo(ConnectionRateLimitResult.Allowed));
        Assert.That(result2, Is.EqualTo(ConnectionRateLimitResult.Allowed));
    }

    [Test]
    public void IPv6Normalization_TreatsEquivalentAddressesAsSame()
    {
        var config = new ConnectionRateLimitConfig { MaxConnectionsPerIp = 1 };
        using var limiter = new ConnectionRateLimiter(config);

        // These are the same IPv6 address in different formats
        limiter.TryAcquire("::1");
        var result = limiter.TryAcquire("0:0:0:0:0:0:0:1");

        Assert.That(result, Is.EqualTo(ConnectionRateLimitResult.PerIpConcurrentLimitExceeded));
    }

    [Test]
    public void GetStats_ReturnsCorrectValues()
    {
        var config = new ConnectionRateLimitConfig { MaxConcurrentConnections = 10 };
        using var limiter = new ConnectionRateLimiter(config);

        limiter.TryAcquire("192.168.1.1");
        limiter.TryAcquire("192.168.1.2");

        var stats = limiter.GetStats();

        Assert.That(stats.CurrentConnections, Is.EqualTo(2));
        Assert.That(stats.TotalAccepted, Is.EqualTo(2));
        Assert.That(stats.UniqueIpCount, Is.EqualTo(2));
    }

    [Test]
    public void ConfigValidation_ThrowsOnInvalidValues()
    {
        var config = new ConnectionRateLimitConfig();

        Assert.Throws<ArgumentOutOfRangeException>(() => config.MaxConcurrentConnections = -1);
        Assert.Throws<ArgumentOutOfRangeException>(() => config.MaxConcurrentConnections = 100_001);
        Assert.Throws<ArgumentOutOfRangeException>(() => config.PerIpWindowSeconds = 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => config.BanThreshold = 0);
    }

    [Test]
    public void IsEnabled_ReturnsFalseWhenAllZero()
    {
        var config = new ConnectionRateLimitConfig
        {
            // Explicitly disable all limits (defaults are now enabled)
            MaxConcurrentConnections = 0,
            GlobalConnectionsPerSecond = 0,
            MaxConnectionsPerIp = 0,
            ConnectionsPerIpPerWindow = 0
        };
        Assert.That(config.IsEnabled, Is.False);
    }

    [Test]
    public void IsEnabled_ReturnsTrueWithDefaults()
    {
        // Defaults now have rate limiting enabled
        var config = new ConnectionRateLimitConfig();
        Assert.That(config.IsEnabled, Is.True);
    }

    [Test]
    public void IsEnabled_ReturnsTrueWhenAnySet()
    {
        var config = new ConnectionRateLimitConfig { MaxConcurrentConnections = 1 };
        Assert.That(config.IsEnabled, Is.True);
    }

    [Test]
    public void WhitelistedIp_BypassesAllLimits()
    {
        var config = new ConnectionRateLimitConfig
        {
            MaxConnectionsPerIp = 1,
            MaxConcurrentConnections = 1,
            WhitelistedIps = new HashSet<string> { "10.0.0.1" }
        };
        using var limiter = new ConnectionRateLimiter(config);

        // Whitelisted IP bypasses all limits
        var result1 = limiter.TryAcquire("10.0.0.1");
        var result2 = limiter.TryAcquire("10.0.0.1");
        var result3 = limiter.TryAcquire("10.0.0.1");

        Assert.That(result1, Is.EqualTo(ConnectionRateLimitResult.Allowed));
        Assert.That(result2, Is.EqualTo(ConnectionRateLimitResult.Allowed));
        Assert.That(result3, Is.EqualTo(ConnectionRateLimitResult.Allowed));
    }

    [Test]
    public void WhitelistedIp_CannotBeBanned()
    {
        var config = new ConnectionRateLimitConfig
        {
            MaxConnectionsPerIp = 1,
            BanThreshold = 1,
            BanDurationSeconds = 60,
            WhitelistedIps = new HashSet<string> { "10.0.0.1" }
        };
        using var limiter = new ConnectionRateLimiter(config);

        // Non-whitelisted IP gets banned
        limiter.TryAcquire("192.168.1.1");
        limiter.TryAcquire("192.168.1.1"); // Violation -> banned
        limiter.Release("192.168.1.1");
        var bannedResult = limiter.TryAcquire("192.168.1.1");
        Assert.That(bannedResult, Is.EqualTo(ConnectionRateLimitResult.IpBanned));

        // Whitelisted IP still works
        var whitelistedResult = limiter.TryAcquire("10.0.0.1");
        Assert.That(whitelistedResult, Is.EqualTo(ConnectionRateLimitResult.Allowed));
    }

    [Test]
    public void WhitelistedIp_NormalizesIPv6()
    {
        var config = new ConnectionRateLimitConfig
        {
            MaxConnectionsPerIp = 1,
            WhitelistedIps = new HashSet<string> { "::1" }
        };
        using var limiter = new ConnectionRateLimiter(config);

        // IPv6 variations should all be recognized as whitelisted
        var result1 = limiter.TryAcquire("::1");
        var result2 = limiter.TryAcquire("0:0:0:0:0:0:0:1");

        Assert.That(result1, Is.EqualTo(ConnectionRateLimitResult.Allowed));
        Assert.That(result2, Is.EqualTo(ConnectionRateLimitResult.Allowed));
    }

    [Test]
    public void WhitelistedIp_StillCountsForStats()
    {
        var config = new ConnectionRateLimitConfig
        {
            MaxConcurrentConnections = 10,
            WhitelistedIps = new HashSet<string> { "10.0.0.1" }
        };
        using var limiter = new ConnectionRateLimiter(config);

        limiter.TryAcquire("10.0.0.1");
        limiter.TryAcquire("10.0.0.1");

        var stats = limiter.GetStats();
        Assert.That(stats.CurrentConnections, Is.EqualTo(2));
        Assert.That(stats.TotalAccepted, Is.EqualTo(2));
    }

    [Test]
    public void ManyUniqueIps_AllAllowed()
    {
        var config = new ConnectionRateLimitConfig
        {
            MaxConcurrentConnections = 0,
            GlobalConnectionsPerSecond = 0,
            MaxConnectionsPerIp = 5,
            ConnectionsPerIpPerWindow = 0
        };
        using var limiter = new ConnectionRateLimiter(config);

        var uniqueIps = 1000;
        var results = new List<ConnectionRateLimitResult>();

        for (int i = 0; i < uniqueIps; i++)
        {
            var ip = $"10.{i / 256}.{i % 256}.1";
            results.Add(limiter.TryAcquire(ip));
        }

        Assert.That(results.All(r => r == ConnectionRateLimitResult.Allowed), Is.True);

        var stats = limiter.GetStats();
        Assert.That(stats.UniqueIpCount, Is.EqualTo(uniqueIps));
    }

    [Test]
    public void MultipleBans_TracksCorrectly()
    {
        var config = new ConnectionRateLimitConfig
        {
            MaxConnectionsPerIp = 1,
            BanThreshold = 1,
            BanDurationSeconds = 3600
        };
        using var limiter = new ConnectionRateLimiter(config);

        for (int i = 0; i < 100; i++)
        {
            var ip = $"192.168.{i / 256}.{i % 256}";
            limiter.TryAcquire(ip);
            limiter.TryAcquire(ip);
        }

        var stats = limiter.GetStats();
        Assert.That(stats.BannedIpCount, Is.GreaterThan(0));
    }

}
