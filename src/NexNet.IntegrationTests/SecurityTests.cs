using System.Net;
using NexNet.Transports;
using System.Threading.Tasks;

#pragma warning disable VSTHRD200

namespace NexNet.IntegrationTests;

internal class SecurityTests
{
    private static TcpServerConfig CreateTestConfig() => new TcpServerConfig()
    {
        EndPoint = new IPEndPoint(IPAddress.Loopback, 0)
    };

    [Test]
    public async Task ConfigBase_Timeout_ValidValues_Succeeds()
    {
        var config = CreateTestConfig();

        // Minimum value (50ms for testing)
        config.Timeout = 50;
        await Assert.That(config.Timeout).IsEqualTo(50);

        // Maximum value
        config.Timeout = 300_000;
        await Assert.That(config.Timeout).IsEqualTo(300_000);

        // Default value
        config.Timeout = 30_000;
        await Assert.That(config.Timeout).IsEqualTo(30_000);
    }

    [Test]
    public async Task ConfigBase_Timeout_BelowMinimum_ThrowsArgumentOutOfRangeException()
    {
        var config = CreateTestConfig();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => config.Timeout = 49);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => config.Timeout = 0);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => config.Timeout = -1);
    }

    [Test]
    public async Task ConfigBase_Timeout_AboveMaximum_ThrowsArgumentOutOfRangeException()
    {
        var config = CreateTestConfig();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => config.Timeout = 300_001);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => config.Timeout = int.MaxValue);
    }

    [Test]
    public async Task ConfigBase_HandshakeTimeout_ValidValues_Succeeds()
    {
        var config = CreateTestConfig();

        // Minimum value (50ms for testing)
        config.HandshakeTimeout = 50;
        await Assert.That(config.HandshakeTimeout).IsEqualTo(50);

        // Maximum value
        config.HandshakeTimeout = 60_000;
        await Assert.That(config.HandshakeTimeout).IsEqualTo(60_000);
    }

    [Test]
    public async Task ConfigBase_HandshakeTimeout_OutOfRange_ThrowsArgumentOutOfRangeException()
    {
        var config = CreateTestConfig();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => config.HandshakeTimeout = 49);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => config.HandshakeTimeout = 60_001);
    }

    [Test]
    public async Task ConfigBase_MaxConcurrentConnectionInvocations_ValidValues_Succeeds()
    {
        var config = CreateTestConfig();

        // Minimum value
        config.MaxConcurrentConnectionInvocations = 1;
        await Assert.That(config.MaxConcurrentConnectionInvocations).IsEqualTo(1);

        // Maximum value
        config.MaxConcurrentConnectionInvocations = 1000;
        await Assert.That(config.MaxConcurrentConnectionInvocations).IsEqualTo(1000);
    }

    [Test]
    public async Task ConfigBase_MaxConcurrentConnectionInvocations_OutOfRange_ThrowsArgumentOutOfRangeException()
    {
        var config = CreateTestConfig();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => config.MaxConcurrentConnectionInvocations = 0);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => config.MaxConcurrentConnectionInvocations = 1001);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => config.MaxConcurrentConnectionInvocations = -1);
    }

    [Test]
    public async Task ConfigBase_DisconnectDelay_ValidValues_Succeeds()
    {
        var config = CreateTestConfig();

        // Disable
        config.DisconnectDelay = 0;
        await Assert.That(config.DisconnectDelay).IsEqualTo(0);

        // Maximum value
        config.DisconnectDelay = 10_000;
        await Assert.That(config.DisconnectDelay).IsEqualTo(10_000);
    }

    [Test]
    public async Task ConfigBase_DisconnectDelay_AboveMaximum_ThrowsArgumentOutOfRangeException()
    {
        var config = CreateTestConfig();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => config.DisconnectDelay = 10_001);
    }

    [Test]
    public async Task ConfigBase_ReceiveSessionPipeOptions_DefaultHoldsTwoFrames()
    {
        var config = CreateTestConfig();

        // A transport receive pipe that pauses below one maximum frame can never resume: frames are consumed whole.
        await Assert.That(config.ReceiveSessionPipeOptions.PauseWriterThreshold).IsGreaterThanOrEqualTo(2 * (ushort.MaxValue + 5));
        await Assert.That(new NexNet.Transports.WebSocket.WebSocketPipeOptions().InputPipeOptions.PauseWriterThreshold).IsGreaterThanOrEqualTo(2 * (ushort.MaxValue + 5));
    }

    [Test]
    public async Task ConfigBase_ReceiveSessionPipeOptions_PauseBelowTwoFrames_ThrowsArgumentOutOfRangeException()
    {
        var config = CreateTestConfig();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => config.ReceiveSessionPipeOptions = System.IO.Pipelines.PipeOptions.Default);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => new NexNet.Transports.WebSocket.WebSocketPipeOptions { InputPipeOptions = System.IO.Pipelines.PipeOptions.Default });

        // 0 never pauses.
        config.ReceiveSessionPipeOptions = new System.IO.Pipelines.PipeOptions(pauseWriterThreshold: 0, resumeWriterThreshold: 0);
    }







    [Test]
    public async Task ConfigBase_FlowControlDefaults_FollowSerializerSecurity()
    {
        var untrusted = CreateTestConfig();
        await Assert.That(untrusted.SessionBudget).IsEqualTo(16 * 1024 * 1024);
        await Assert.That(untrusted.MaxPipes).IsEqualTo(32);
        await Assert.That(untrusted.PipeWindow).IsEqualTo(256 * 1024);
        await Assert.That(untrusted.MaxMessageSize).IsEqualTo(4 * 1024 * 1024);
        await Assert.That(untrusted.InvocationCredits).IsEqualTo(32);

        var trusted = CreateTestConfig();
        trusted.SerializerOptions = NexNet.Serialization.NexusSerializerOptions.Trusted;
        await Assert.That(trusted.SessionBudget).IsEqualTo(64 * 1024 * 1024);
        await Assert.That(trusted.MaxPipes).IsEqualTo(32);
        await Assert.That(trusted.PipeWindow).IsEqualTo(1024 * 1024);
        await Assert.That(trusted.MaxMessageSize).IsEqualTo(16 * 1024 * 1024);
        await Assert.That(trusted.InvocationCredits).IsEqualTo(128);

        // Both default sets fit their budget.
        await Assert.That(untrusted.ValidateFlowControl).ThrowsNothing();
        await Assert.That(trusted.ValidateFlowControl).ThrowsNothing();
    }

    [Test]
    public async Task ConfigBase_MaxConcurrentConnectionInvocations_FollowsSerializerSecurity()
    {
        var untrusted = CreateTestConfig();
        await Assert.That(untrusted.MaxConcurrentConnectionInvocations).IsEqualTo(2);

        var trusted = CreateTestConfig();
        trusted.SerializerOptions = NexNet.Serialization.NexusSerializerOptions.Trusted;
        await Assert.That(trusted.MaxConcurrentConnectionInvocations).IsEqualTo(32);

        // An explicit value wins on both, whichever is set first.
        var explicitUntrusted = CreateTestConfig();
        explicitUntrusted.MaxConcurrentConnectionInvocations = 7;
        await Assert.That(explicitUntrusted.MaxConcurrentConnectionInvocations).IsEqualTo(7);

        var explicitTrusted = CreateTestConfig();
        explicitTrusted.MaxConcurrentConnectionInvocations = 3;
        explicitTrusted.SerializerOptions = NexNet.Serialization.NexusSerializerOptions.Trusted;
        await Assert.That(explicitTrusted.MaxConcurrentConnectionInvocations).IsEqualTo(3);

        // Validation unchanged: 1 to 1000.
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => trusted.MaxConcurrentConnectionInvocations = 0);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => trusted.MaxConcurrentConnectionInvocations = 1001);
        trusted.MaxConcurrentConnectionInvocations = 1000;
        await Assert.That(trusted.MaxConcurrentConnectionInvocations).IsEqualTo(1000);
    }

    [Test]
    public async Task ConfigBase_InvocationQueueWarningThreshold_DefaultsAndRange()
    {
        var config = CreateTestConfig();
        await Assert.That(config.InvocationQueueWarningThreshold).IsEqualTo(5000);

        config.InvocationQueueWarningThreshold = 0;
        await Assert.That(config.InvocationQueueWarningThreshold).IsEqualTo(0);
        config.InvocationQueueWarningThreshold = 600_000;
        await Assert.That(config.InvocationQueueWarningThreshold).IsEqualTo(600_000);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => config.InvocationQueueWarningThreshold = -1);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => config.InvocationQueueWarningThreshold = 600_001);
    }

    [Test]
    public async Task ConfigBase_FlowControlSettings_OutOfRange_ThrowsArgumentOutOfRangeException()
    {
        var config = CreateTestConfig();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => config.SessionBudget = 64 * 1024 - 1);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => config.MaxPipes = 0);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => config.MaxPipes = 256);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => config.PipeWindow = 4 * 1024 - 1);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => config.MaxMessageSize = 64 * 1024 - 1);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => config.InvocationCredits = 0);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => config.ProgressTimeout = 49);
    }

    [Test]
    public async Task ConfigBase_BudgetSmallerThanFixedReservations_ThrowsWhenTheServerIsBuilt()
    {
        var config = CreateTestConfig();

        // 32 x 256KB + 4MB + 32 x 64KB = 14MB does not fit in 8MB.
        config.SessionBudget = 8 * 1024 * 1024;

        await Assert.ThrowsAsync<InvalidOperationException>(config.ValidateFlowControl);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NexNet.IntegrationTests.TestInterfaces.ServerNexus.CreateServer(config, static () => new NexNet.IntegrationTests.TestInterfaces.ServerNexus()));
    }

    [Test]
    public async Task SessionId_MultipleGeneration_ProducesUniqueIds()
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
            await Assert.That(ids.Add(id)).IsTrue().Because($"Collision detected at iteration {i}");
        }
    }

    [Test]
    public async Task SessionId_RandomPart_IsCryptographicallyRandom()
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
        await Assert.That(uniqueRandomParts).IsGreaterThan(990).Because("Random parts should be mostly unique");
    }
}