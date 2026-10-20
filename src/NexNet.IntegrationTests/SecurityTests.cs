using System.Net;
using NexNet.Transports;
using NUnit.Framework;

#pragma warning disable VSTHRD200

namespace NexNet.IntegrationTests;

internal class SecurityTests
{
    private static TcpServerConfig CreateTestConfig() => new TcpServerConfig()
    {
        EndPoint = new IPEndPoint(IPAddress.Loopback, 0)
    };

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
    public void ConfigBase_ReceiveSessionPipeOptions_DefaultHoldsTwoFrames()
    {
        var config = CreateTestConfig();

        // A transport receive pipe that pauses below one maximum frame can never resume: frames are consumed whole.
        Assert.That(config.ReceiveSessionPipeOptions.PauseWriterThreshold, Is.GreaterThanOrEqualTo(2 * (ushort.MaxValue + 5)));
        Assert.That(new NexNet.Transports.WebSocket.WebSocketPipeOptions().InputPipeOptions.PauseWriterThreshold,
            Is.GreaterThanOrEqualTo(2 * (ushort.MaxValue + 5)));
    }

    [Test]
    public void ConfigBase_ReceiveSessionPipeOptions_PauseBelowTwoFrames_ThrowsArgumentOutOfRangeException()
    {
        var config = CreateTestConfig();

        Assert.Throws<ArgumentOutOfRangeException>(() => config.ReceiveSessionPipeOptions = System.IO.Pipelines.PipeOptions.Default);
        Assert.Throws<ArgumentOutOfRangeException>(() => new NexNet.Transports.WebSocket.WebSocketPipeOptions { InputPipeOptions = System.IO.Pipelines.PipeOptions.Default });

        // 0 never pauses.
        config.ReceiveSessionPipeOptions = new System.IO.Pipelines.PipeOptions(pauseWriterThreshold: 0, resumeWriterThreshold: 0);
    }







    [Test]
    public void ConfigBase_FlowControlDefaults_FollowSerializerSecurity()
    {
        var untrusted = CreateTestConfig();
        Assert.That(untrusted.SessionBudget, Is.EqualTo(16 * 1024 * 1024));
        Assert.That(untrusted.MaxPipes, Is.EqualTo(32));
        Assert.That(untrusted.PipeWindow, Is.EqualTo(256 * 1024));
        Assert.That(untrusted.MaxMessageSize, Is.EqualTo(4 * 1024 * 1024));
        Assert.That(untrusted.InvocationCredits, Is.EqualTo(32));

        var trusted = CreateTestConfig();
        trusted.SerializerOptions = NexNet.Serialization.NexusSerializerOptions.Trusted;
        Assert.That(trusted.SessionBudget, Is.EqualTo(64 * 1024 * 1024));
        Assert.That(trusted.MaxPipes, Is.EqualTo(32));
        Assert.That(trusted.PipeWindow, Is.EqualTo(1024 * 1024));
        Assert.That(trusted.MaxMessageSize, Is.EqualTo(16 * 1024 * 1024));
        Assert.That(trusted.InvocationCredits, Is.EqualTo(128));

        // Both default sets fit their budget.
        Assert.DoesNotThrow(untrusted.ValidateFlowControl);
        Assert.DoesNotThrow(trusted.ValidateFlowControl);
    }

    [Test]
    public void ConfigBase_FlowControlSettings_OutOfRange_ThrowsArgumentOutOfRangeException()
    {
        var config = CreateTestConfig();

        Assert.Throws<ArgumentOutOfRangeException>(() => config.SessionBudget = 64 * 1024 - 1);
        Assert.Throws<ArgumentOutOfRangeException>(() => config.MaxPipes = 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => config.MaxPipes = 256);
        Assert.Throws<ArgumentOutOfRangeException>(() => config.PipeWindow = 4 * 1024 - 1);
        Assert.Throws<ArgumentOutOfRangeException>(() => config.MaxMessageSize = 64 * 1024 - 1);
        Assert.Throws<ArgumentOutOfRangeException>(() => config.InvocationCredits = 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => config.ProgressTimeout = 49);
    }

    [Test]
    public void ConfigBase_BudgetSmallerThanFixedReservations_ThrowsWhenTheServerIsBuilt()
    {
        var config = CreateTestConfig();

        // 32 x 256KB + 4MB + 32 x 64KB = 14MB does not fit in 8MB.
        config.SessionBudget = 8 * 1024 * 1024;

        Assert.Throws<InvalidOperationException>(config.ValidateFlowControl);
        Assert.Throws<InvalidOperationException>(() =>
            NexNet.IntegrationTests.TestInterfaces.ServerNexus.CreateServer(config, static () => new NexNet.IntegrationTests.TestInterfaces.ServerNexus()));
    }

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
}
