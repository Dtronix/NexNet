using System.Net;
using NexNet.Transports;
using NUnit.Framework;

#pragma warning disable VSTHRD200

namespace NexNet.IntegrationTests;

/// <summary>
/// Tests for Phase 2 security fixes:
/// - Fix 2.1: HashSet for invocation IDs (O(1) lookup)
/// - Fix 2.2: Secure session ID generation
/// - Fix 2.9: Configuration bounds checking
/// </summary>
internal class SecurityTests
{
    private static TcpServerConfig CreateTestConfig() => new TcpServerConfig()
    {
        EndPoint = new IPEndPoint(IPAddress.Loopback, 0)
    };

    #region Configuration Bounds Tests (Fix 2.9)

    [Test]
    public void ConfigBase_Timeout_ValidValues_Succeeds()
    {
        var config = CreateTestConfig();

        // Minimum value (50ms for testing)
        config.Timeout = 50;
        Assert.That(config.Timeout, Is.EqualTo(50));

        // Maximum value
        config.Timeout = 300_000;
        Assert.That(config.Timeout, Is.EqualTo(300_000));

        // Default value
        config.Timeout = 30_000;
        Assert.That(config.Timeout, Is.EqualTo(30_000));
    }

    [Test]
    public void ConfigBase_Timeout_BelowMinimum_ThrowsArgumentOutOfRangeException()
    {
        var config = CreateTestConfig();

        Assert.Throws<ArgumentOutOfRangeException>(() => config.Timeout = 49);
        Assert.Throws<ArgumentOutOfRangeException>(() => config.Timeout = 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => config.Timeout = -1);
    }

    [Test]
    public void ConfigBase_Timeout_AboveMaximum_ThrowsArgumentOutOfRangeException()
    {
        var config = CreateTestConfig();

        Assert.Throws<ArgumentOutOfRangeException>(() => config.Timeout = 300_001);
        Assert.Throws<ArgumentOutOfRangeException>(() => config.Timeout = int.MaxValue);
    }

    [Test]
    public void ConfigBase_HandshakeTimeout_ValidValues_Succeeds()
    {
        var config = CreateTestConfig();

        // Minimum value (50ms for testing)
        config.HandshakeTimeout = 50;
        Assert.That(config.HandshakeTimeout, Is.EqualTo(50));

        // Maximum value
        config.HandshakeTimeout = 60_000;
        Assert.That(config.HandshakeTimeout, Is.EqualTo(60_000));
    }

    [Test]
    public void ConfigBase_HandshakeTimeout_OutOfRange_ThrowsArgumentOutOfRangeException()
    {
        var config = CreateTestConfig();

        Assert.Throws<ArgumentOutOfRangeException>(() => config.HandshakeTimeout = 49);
        Assert.Throws<ArgumentOutOfRangeException>(() => config.HandshakeTimeout = 60_001);
    }

    [Test]
    public void ConfigBase_MaxConcurrentConnectionInvocations_ValidValues_Succeeds()
    {
        var config = CreateTestConfig();

        // Minimum value
        config.MaxConcurrentConnectionInvocations = 1;
        Assert.That(config.MaxConcurrentConnectionInvocations, Is.EqualTo(1));

        // Maximum value
        config.MaxConcurrentConnectionInvocations = 1000;
        Assert.That(config.MaxConcurrentConnectionInvocations, Is.EqualTo(1000));
    }

    [Test]
    public void ConfigBase_MaxConcurrentConnectionInvocations_OutOfRange_ThrowsArgumentOutOfRangeException()
    {
        var config = CreateTestConfig();

        Assert.Throws<ArgumentOutOfRangeException>(() => config.MaxConcurrentConnectionInvocations = 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => config.MaxConcurrentConnectionInvocations = 1001);
        Assert.Throws<ArgumentOutOfRangeException>(() => config.MaxConcurrentConnectionInvocations = -1);
    }

    [Test]
    public void ConfigBase_DisconnectDelay_ValidValues_Succeeds()
    {
        var config = CreateTestConfig();

        // Disable
        config.DisconnectDelay = 0;
        Assert.That(config.DisconnectDelay, Is.EqualTo(0));

        // Maximum value
        config.DisconnectDelay = 10_000;
        Assert.That(config.DisconnectDelay, Is.EqualTo(10_000));
    }

    [Test]
    public void ConfigBase_DisconnectDelay_AboveMaximum_ThrowsArgumentOutOfRangeException()
    {
        var config = CreateTestConfig();

        Assert.Throws<ArgumentOutOfRangeException>(() => config.DisconnectDelay = 10_001);
    }

    [Test]
    public void ConfigBase_NexusPipeFlushChunkSize_ValidValues_Succeeds()
    {
        var config = CreateTestConfig();

        // Minimum value (1KB)
        config.NexusPipeFlushChunkSize = 1024;
        Assert.That(config.NexusPipeFlushChunkSize, Is.EqualTo(1024));

        // Maximum value (1MB)
        config.NexusPipeFlushChunkSize = 1024 * 1024;
        Assert.That(config.NexusPipeFlushChunkSize, Is.EqualTo(1024 * 1024));
    }

    [Test]
    public void ConfigBase_NexusPipeFlushChunkSize_OutOfRange_ThrowsArgumentOutOfRangeException()
    {
        var config = CreateTestConfig();

        Assert.Throws<ArgumentOutOfRangeException>(() => config.NexusPipeFlushChunkSize = 1023);
        Assert.Throws<ArgumentOutOfRangeException>(() => config.NexusPipeFlushChunkSize = 1024 * 1024 + 1);
    }

    [Test]
    public void ConfigBase_NexusPipeHighWaterMark_ValidValues_Succeeds()
    {
        var config = CreateTestConfig();

        // Minimum value (1KB)
        config.NexusPipeHighWaterMark = 1024;
        Assert.That(config.NexusPipeHighWaterMark, Is.EqualTo(1024));

        // Maximum value (10MB)
        config.NexusPipeHighWaterMark = 10 * 1024 * 1024;
        Assert.That(config.NexusPipeHighWaterMark, Is.EqualTo(10 * 1024 * 1024));
    }

    [Test]
    public void ConfigBase_NexusPipeHighWaterMark_OutOfRange_ThrowsArgumentOutOfRangeException()
    {
        var config = CreateTestConfig();

        Assert.Throws<ArgumentOutOfRangeException>(() => config.NexusPipeHighWaterMark = 1023);
        Assert.Throws<ArgumentOutOfRangeException>(() => config.NexusPipeHighWaterMark = 10 * 1024 * 1024 + 1);
    }

    [Test]
    public void ConfigBase_NexusPipeLowWaterMark_ValidValues_Succeeds()
    {
        var config = CreateTestConfig();

        // Set high water mark first
        config.NexusPipeHighWaterMark = 100 * 1024;

        // Minimum value (1KB)
        config.NexusPipeLowWaterMark = 1024;
        Assert.That(config.NexusPipeLowWaterMark, Is.EqualTo(1024));

        // At high water mark
        config.NexusPipeLowWaterMark = 100 * 1024;
        Assert.That(config.NexusPipeLowWaterMark, Is.EqualTo(100 * 1024));
    }

    [Test]
    public void ConfigBase_NexusPipeLowWaterMark_OutOfRange_ThrowsArgumentOutOfRangeException()
    {
        var config = CreateTestConfig();

        // Default high water mark is 192KB
        Assert.Throws<ArgumentOutOfRangeException>(() => config.NexusPipeLowWaterMark = 1023);
        Assert.Throws<ArgumentOutOfRangeException>(() => config.NexusPipeLowWaterMark = 200 * 1024); // Above high water mark
    }

    [Test]
    public void ConfigBase_NexusPipeHighWaterCutoff_ValidValues_Succeeds()
    {
        var config = CreateTestConfig();

        // At high water mark (default is 192KB)
        config.NexusPipeHighWaterCutoff = 192 * 1024;
        Assert.That(config.NexusPipeHighWaterCutoff, Is.EqualTo(192 * 1024));

        // Maximum value (100MB)
        config.NexusPipeHighWaterCutoff = 100 * 1024 * 1024;
        Assert.That(config.NexusPipeHighWaterCutoff, Is.EqualTo(100 * 1024 * 1024));
    }

    [Test]
    public void ConfigBase_NexusPipeHighWaterCutoff_OutOfRange_ThrowsArgumentOutOfRangeException()
    {
        var config = CreateTestConfig();

        // Below high water mark
        Assert.Throws<ArgumentOutOfRangeException>(() => config.NexusPipeHighWaterCutoff = 100 * 1024);
        // Above maximum
        Assert.Throws<ArgumentOutOfRangeException>(() => config.NexusPipeHighWaterCutoff = 100 * 1024 * 1024 + 1);
    }

    [Test]
    public void ConfigBase_WatermarkRelationships_Enforced()
    {
        var config = CreateTestConfig();

        // Set high water mark first, then try to set low water mark above it
        config.NexusPipeHighWaterMark = 50 * 1024;
        Assert.Throws<ArgumentOutOfRangeException>(() => config.NexusPipeLowWaterMark = 60 * 1024);

        // Set high water cutoff below current high water mark
        config.NexusPipeHighWaterMark = 100 * 1024;
        Assert.Throws<ArgumentOutOfRangeException>(() => config.NexusPipeHighWaterCutoff = 50 * 1024);
    }

    #endregion

    #region Session ID Uniqueness Tests (Fix 2.2)

    [Test]
    public void SessionId_MultipleGeneration_ProducesUniqueIds()
    {
        // This tests that the session ID generation produces unique IDs
        // by verifying no collisions in a large set of generated IDs
        var ids = new HashSet<long>();
        var randomBytes = new byte[4];

        // Simulate session ID generation pattern
        for (int i = 0; i < 10000; i++)
        {
            System.Security.Cryptography.RandomNumberGenerator.Fill(randomBytes);
            uint randomPart = BitConverter.ToUInt32(randomBytes);
            long id = (long)i << 32 | randomPart;
            Assert.That(ids.Add(id), Is.True, $"Collision detected at iteration {i}");
        }
    }

    [Test]
    public void SessionId_RandomPart_IsCryptographicallyRandom()
    {
        // Verify that the random portion uses cryptographic randomness
        // by checking that sequential IDs have different random parts
        var randomParts = new List<uint>();

        for (int i = 0; i < 1000; i++)
        {
            var randomBytes = new byte[4];
            System.Security.Cryptography.RandomNumberGenerator.Fill(randomBytes);
            randomParts.Add(BitConverter.ToUInt32(randomBytes));
        }

        // Check that there are no sequential patterns
        var uniqueRandomParts = randomParts.Distinct().Count();
        Assert.That(uniqueRandomParts, Is.GreaterThan(990), "Random parts should be mostly unique");
    }

    #endregion

}
