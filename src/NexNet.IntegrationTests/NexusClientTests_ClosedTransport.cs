using System.IO.Pipelines;
using System.Security.Cryptography.X509Certificates;
using NexNet.IntegrationTests.Metrics;
using NexNet.IntegrationTests.TestInterfaces;
using NexNet.Transports;
#pragma warning disable VSTHRD200

namespace NexNet.IntegrationTests;

/// <summary>
/// A transport that is already closed for writing when the client connects.
/// </summary>
internal partial class NexusClientTests
{
    [Test]
    public async Task TransportClosedForWriting_ConnectFailsWithoutWaitingForTheWriteLock()
    {
        var logger = new CapturingLogger();
        var client = ClientNexus.CreateClient(new ClosedOutputClientConfig { Logger = logger }, new ClientNexus());

        // The first send finds the transport closed and disconnects. It used to disconnect while still holding the
        // write lock, so the disconnect waited out its 5 s write-lock timeout before the connect failed.
        var result = await client.TryConnectAsync().Timeout(T(TestTimeouts.Connect));

        await Assert.That(result.Success).IsFalse();
        await Assert.That(client.State).IsEqualTo(ConnectionState.Disconnected);
        await Assert.That(logger.Warnings.Any(w => w.Contains("write lock"))).IsFalse();
    }

    /// <summary>A custom transport whose output was closed by the peer: every flush returns completed.</summary>
    private sealed class ClosedOutputClientConfig : ClientConfig
    {
        protected internal override bool IsSecureTransport => true;

        protected override ValueTask<ITransport> OnConnectTransport(CancellationToken cancellationToken)
            => ValueTask.FromResult<ITransport>(new ClosedOutputTransport());
    }

    private sealed class ClosedOutputTransport : ITransport
    {
        private readonly Pipe _input = new();
        private readonly Pipe _output = new();

        public ClosedOutputTransport() => _output.Reader.Complete();

        public PipeReader Input => _input.Reader;
        public PipeWriter Output => _output.Writer;
        public string? RemoteAddress => null;
        public int? RemotePort => null;
        public X509Certificate2? RemoteCertificate => null;

        public ValueTask CloseAsync(bool linger)
        {
            _input.Writer.Complete();
            return ValueTask.CompletedTask;
        }
    }
}
