using System.Net.Sockets;
using NexNet.Inspector.Core.Findings;
using NexNet.Inspector.Testing;
using NexNet.Inspector.Tests.Tap;
using NexNet.Transports;
using NexNet.Transports.Uds;

namespace NexNet.Inspector.Tests.Testing;

/// <summary>
/// The test helper (NexNet.Inspector.Testing): an integration-style test records its own traffic with
/// <see cref="InspectorCapture"/> and reads it back as the text format's summary and findings. Here rather than in
/// NexNet.IntegrationTests because that suite does not build its nexuses with the inspector schema nor reference the
/// inspector projects; this shows the same pattern on the tap tests' nexus.
/// </summary>
public class InspectorCaptureTests
{
    private static string SocketPath() => Path.Combine(Path.GetTempPath(), $"nexnet-capture-{Guid.NewGuid():N}.sock");

    [Test]
    public async Task A_test_records_its_traffic_and_reads_the_summary_and_findings()
    {
        var path = SocketPath();
        var serverConfig = new UdsServerConfig { EndPoint = new UnixDomainSocketEndPoint(path) };
        var report = new StringWriter { NewLine = "\n" };
        var capture = InspectorCapture.Start<TapServerNexus>(serverConfig, new InspectorCaptureOptions { Output = report });
        await using (capture)
        {
            var server = TapServerNexus.CreateServer(serverConfig, () => new TapServerNexus());
            await server.StartAsync();
            var client = TapClientNexus.CreateClient(new UdsClientConfig { EndPoint = new UnixDomainSocketEndPoint(path) }, new TapClientNexus());
            try
            {
                await client.ConnectAsync();
                for (var i = 0; i < 3; i++)
                    await client.Proxy.Echo([1, 2, 3]);

                // On demand while it records: what was written so far; the connected session reads as cut off there.
                string live = "";
                for (var i = 0; i < 200 && !live.Contains("Echo ×3", StringComparison.Ordinal); i++)
                {
                    await Task.Delay(10);
                    live = capture.Summary();
                }

                await Assert.That(live).Contains("Echo ×3 ok 3");
                await Assert.That(live).Contains("END truncated");

                await client.DisconnectAsync();
            }
            finally
            {
                await client.DisposeAsync();
                await server.StopAsync();
                await server.DisposeAsync();
                File.Delete(path);
            }

            await capture.StopAsync();
            await Assert.That(capture.IsStopped).IsTrue();
            var summary = capture.Summary();
            await Assert.That(summary).StartsWith("@0x");
            await Assert.That(summary).Contains("schema TapServerNexus#");
            await Assert.That(summary).Contains("Echo ×3 ok 3");
            await Assert.That(summary).Contains("END ");
            await Assert.That(capture.FindingList().Where(f => f.Severity >= FindingSeverity.Warning).ToArray()).IsEmpty()
                .Because(capture.Findings());
            await Assert.That(capture.Findings()).Contains(" server uds ");
            await Assert.That(capture.Text()).Contains("Echo(");
            await Assert.That(capture.ToArray().Length).IsGreaterThan(0);
        }

        // Disposing wrote the summary and the findings to the test's output.
        var written = report.ToString();
        await Assert.That(written).Contains("Echo ×3 ok 3");
        await Assert.That(written).Contains("findings ");
    }

    [Test]
    public async Task Nothing_recorded_yet_reads_no_sessions()
    {
        var config = new UdsServerConfig { EndPoint = new UnixDomainSocketEndPoint(SocketPath()) };
        await using var capture = InspectorCapture.Start(config);
        await Assert.That(capture.Summary()).IsEqualTo("no sessions\n");
        await Assert.That(capture.FindingList()).IsEmpty();
        await Assert.That(capture.Inspector.Role).IsEqualTo(Core.Format.NntlSessionRole.Server);
    }
}
