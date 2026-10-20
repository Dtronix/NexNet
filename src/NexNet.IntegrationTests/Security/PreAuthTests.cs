using NexNet.IntegrationTests.TestInterfaces;
using NexNet.Internals;
using NexNet.Invocation;
using NexNet.Messages;
using NUnit.Framework;

#pragma warning disable VSTHRD200

namespace NexNet.IntegrationTests.Security;

/// <summary>
/// What a server accepts before a connection completes authentication (docs/internals/flow-control-v2.md, section 7).
/// </summary>
internal class PreAuthTests : BaseTests
{
    [Test]
    public void MessageTypes_NonDisconnectTypesStayOutsideDisconnectRange()
    {
        // The read loop treats every byte in 20-38 as a disconnect reason before it applies the pre-auth allow-list.
        foreach (var type in Enum.GetValues<MessageType>())
        {
            var isDisconnect = type.ToString().StartsWith("Disconnect", StringComparison.Ordinal);
            Assert.That((byte)type is >= 20 and <= 38, Is.EqualTo(isDisconnect), $"{type} = {(byte)type}");
        }
    }

    private static IEnumerable<MessageType> TypesOtherThanClientGreetings() => Enum.GetValues<MessageType>()
        .Where(t => t is not MessageType.ClientGreeting and not MessageType.ClientGreetingReconnection);

    [TestCaseSource(nameof(TypesOtherThanClientGreetings))]
    public async Task AnyMessageBeforeGreeting_Disconnects(MessageType type)
    {
        var serverConfig = CreateServerConfig(Type.Tcp);
        var server = CreateServer(serverConfig, null);
        await server.StartAsync();

        using var client = new RawTcpClient(serverConfig, false, CurrentTcpPort!.Value, Logger);
        await client.ConnectAsync();
        await client.SendProtocolHeaderAsync();
        await client.ReadProtocolHeaderAsync();

        // Type byte plus an empty body length; a type without a body ignores the rest.
        client.Write([(byte)type, 0, 0]);

        // The server closes the connection. A disconnect type is the peer leaving, so it is closed without a reason;
        // anything else is a protocol error.
        var received = await ReadUntilClosed(client).Timeout(2);
        if (type.ToString().StartsWith("Disconnect", StringComparison.Ordinal))
            Assert.That(received, Is.Empty.Or.EqualTo(new[] { (byte)DisconnectReason.ProtocolError }));
        else
            Assert.That(received, Is.EqualTo(new[] { (byte)DisconnectReason.ProtocolError }));
    }

    [Test]
    public async Task OversizedFrameHeaderBeforeGreeting_DisconnectsWithoutBody()
    {
        var serverConfig = CreateServerConfig(Type.Tcp);
        var server = CreateServer(serverConfig, null);
        await server.StartAsync();

        using var client = new RawTcpClient(serverConfig, false, CurrentTcpPort!.Value, Logger);
        await client.ConnectAsync();
        await client.SendProtocolHeaderAsync();
        await client.ReadProtocolHeaderAsync();

        // Declares a 64 KiB greeting and sends no body: refused at the header.
        client.Write([(byte)MessageType.ClientGreeting, 0xFF, 0xFF]);

        await client.AssertDisconnectReason(DisconnectReason.ProtocolError).Timeout(1);
    }

    [Test]
    public async Task FrameAfterGreeting_DuringSlowAuthentication_DisconnectsWithoutWaiting()
    {
        var serverConfig = CreateServerConfig(Type.Tcp);
        serverConfig.Authenticate = true;
        var authenticating = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = CreateServer(serverConfig, nexus => nexus.OnAuthenticateEvent = async _ =>
        {
            authenticating.TrySetResult();
            await Task.Delay(5000);
            return null;
        });
        await server.StartAsync();

        using var client = new RawTcpClient(serverConfig, false, CurrentTcpPort!.Value, Logger);
        await client.ConnectAsync();
        await client.SendProtocolHeaderAsync();
        await client.ReadProtocolHeaderAsync();
        await client.SendMessageAsync(new ClientGreetingMessage
        {
            ServerNexusHash = IInvocationMethodHash.GetMethodHash<ServerNexus>(),
            ClientNexusHash = IInvocationMethodHash.GetMethodHash<ClientNexus>(),
        }).Timeout(1);
        await authenticating.Task.Timeout(1);

        // OnAuthenticate runs off the read loop, so this frame is handled while it is still running.
        client.Write((byte)MessageType.Ping);

        await client.AssertDisconnectReason(DisconnectReason.ProtocolError).Timeout(1);
    }

    [Test]
    public async Task MaxPendingHandshakes_ClosesNewTransportsUntilASlotFrees()
    {
        var serverConfig = CreateServerConfig(Type.Tcp);
        serverConfig.MaxPendingHandshakes = 2;
        var server = CreateServer(serverConfig, null);
        await server.StartAsync();

        // Two connections that never send a greeting hold both slots (a handshake slowloris).
        using var pending1 = new RawTcpClient(serverConfig, false, CurrentTcpPort!.Value, Logger);
        using var pending2 = new RawTcpClient(serverConfig, false, CurrentTcpPort!.Value, Logger);
        foreach (var pending in new[] { pending1, pending2 })
        {
            await pending.ConnectAsync();
            await pending.SendProtocolHeaderAsync();
            await pending.ReadProtocolHeaderAsync();
        }

        // A third is closed straight away, long before the handshake timeout.
        using (var rejected = new RawTcpClient(serverConfig, false, CurrentTcpPort!.Value, Logger))
        {
            await rejected.ConnectAsync();
            Assert.That(await ReadUntilClosed(rejected).Timeout(1), Is.Empty);
        }

        // Ending one pending connection frees its slot.
        pending1.ForceDisconnect();
        await Task.Delay(200);

        using var accepted = new RawTcpClient(serverConfig, false, CurrentTcpPort!.Value, Logger);
        await accepted.ConnectAsync();
        await accepted.SendProtocolHeaderAsync();
        await accepted.ReadProtocolHeaderAsync().Timeout(1);
    }

    [Test]
    public void PreAuthFailureLog_AggregatesPerInterval()
    {
        var logger = new CapturingLogger();
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var log = new PreAuthFailureLog(logger, time);

        // The first failure is logged; the rest of the interval is counted, not logged.
        log.Record(DisconnectReason.ProtocolError);
        for (var i = 0; i < 100; i++)
            log.Record(DisconnectReason.ProtocolError);
        log.RecordHandshakeLimit();
        Assert.That(logger.Warnings, Has.Count.EqualTo(1));

        time.Advance(PreAuthFailureLog.Interval);
        log.Record(DisconnectReason.Timeout);

        Assert.That(logger.Warnings, Has.Count.EqualTo(2));
        Assert.That(logger.Warnings[1], Does.Contain("ProtocolError=100").And.Contain("MaxPendingHandshakes=1").And.Contain("Timeout=1"));
    }

    /// <summary>
    /// Reads until the server closes the connection and returns what it sent.
    /// </summary>
    private static async Task<byte[]> ReadUntilClosed(RawTcpClient client)
    {
        var received = new List<byte>();
        var buffer = new byte[256];
        try
        {
            int read;
            while ((read = await client.Stream!.ReadAsync(buffer)) > 0)
                received.AddRange(buffer.AsSpan(0, read).ToArray());
        }
        catch (IOException)
        {
            // Reset by the server.
        }

        return received.ToArray();
    }

    private sealed class CapturingLogger : NexNet.Logging.INexusLogger
    {
        public List<string> Warnings { get; } = new();
        public NexNet.Logging.NexusLogBehaviors Behaviors { get; set; }
        public string FormattedPath => "test";
        public string? PathSegment { get; set; }

        public void Log(NexNet.Logging.NexusLogLevel logLevel, string? category, Exception? exception, string message)
        {
            if (logLevel == NexNet.Logging.NexusLogLevel.Warning)
                Warnings.Add(message);
        }

        public NexNet.Logging.INexusLogger CreateLogger(string? pathSegment = null) => this;
    }
}
