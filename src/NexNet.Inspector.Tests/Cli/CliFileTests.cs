using System.Text.Json;
using NexNet.Inspector.Cli;
using NexNet.Inspector.Core.Format;
using NexNet.Inspector.Core.Model;
using NexNet.Inspector.Tests.Format;

namespace NexNet.Inspector.Tests.Cli;

/// <summary>nexnet-tap info and repair on recordings built with known contents.</summary>
public class CliFileTests
{
    private static string WriteTemp(byte[] bytes)
    {
        var path = Path.Combine(Path.GetTempPath(), $"nntap-cli-{Guid.NewGuid():N}.nntl");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static async Task<(int Code, string Output, string Error)> RunAsync(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var code = await new TapCli(output, error).RunAsync(args, null, CancellationToken.None);
        return (code, output.ToString(), error.ToString());
    }

    [Test]
    public async Task Info_summarises_every_session()
    {
        var recording = NntlTestData.Build();
        var path = WriteTemp(recording.File);
        try
        {
            RecordingSummary info;
            using (var stream = File.OpenRead(path))
                info = RecordingAnalyzer.Analyze(stream, "x.nntl", messages: false);

            var header = NntlTestData.Header;
            await Assert.That(info.FormatVersion).IsEqualTo(NntlFormat.Version);
            await Assert.That(info.Producer).IsEqualTo(header.Producer);
            await Assert.That(info.Host).IsEqualTo(header.Host);
            await Assert.That(info.WallClockUtc.Ticks).IsEqualTo(header.WallClockUtcTicks);
            await Assert.That(info.IndexSource).IsEqualTo(NntlIndexSource.Footer);
            await Assert.That(info.Schemas).IsEquivalentTo(new[] { Convert.ToHexStringLower(NntlTestData.SchemaHash) });
            await Assert.That(info.Sessions.Select(s => s.Id)).IsEquivalentTo(NntlTestData.SessionIds);

            var first = info.Sessions[0];
            await Assert.That(first.Role).IsEqualTo(NntlSessionRole.Server);
            await Assert.That(first.Transport).IsEqualTo("Tcp");
            await Assert.That(first.Endpoint).IsEqualTo("primary");
            await Assert.That(first.Gaps).IsEqualTo(new GapSummary(1, 17, 4_096));
            await Assert.That(first.SchemaEmbedded).IsTrue();
            await Assert.That(first.Close).IsEqualTo(new CloseSummary(NntlCloseKind.Disconnected, Messages.DisconnectReason.Graceful));
            await Assert.That(first.StartUtc).IsEqualTo(header.ToUtc(1_000));
            await Assert.That(first.EndUtc).IsEqualTo(header.ToUtc(3_000));

            var second = info.Sessions[1];
            await Assert.That(second.Snapshot).IsTrue();
            await Assert.That(second.Close?.Kind).IsEqualTo(NntlCloseKind.RecordingStopped);
            await Assert.That(info.Sessions[2].SchemaHash).IsNull();

            // Op counts and bytes per direction against the builder's own sequence (op k: session k % 3, direction k % 2).
            for (var i = 0; i < 3; i++)
            {
                long inOps = 0, inBytes = 0, outOps = 0, outBytes = 0, redacted = 0;
                for (var k = i; k < 30; k += 3)
                {
                    var length = NntlTestData.OpBytes(k).Length;
                    if (k % 2 == 0)
                    {
                        inOps++;
                        inBytes += length;
                    }
                    else
                    {
                        outOps++;
                        outBytes += length;
                    }

                    if (k == 4)
                        redacted++;
                }

                var s = info.Sessions[i];
                await Assert.That((s.Inbound.Ops, s.Inbound.Bytes)).IsEqualTo((inOps, inBytes));
                await Assert.That((s.Outbound.Ops, s.Outbound.Bytes)).IsEqualTo((outOps, outBytes));
                await Assert.That(s.Inbound.RedactedOps + s.Outbound.RedactedOps).IsEqualTo(redacted);
            }

            // The synthetic ops are not NexNet frames: framing reports it instead of failing.
            await Assert.That(first.Outbound.Framing).IsEqualTo(FramingState.StoppedAtGap);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task Info_json_is_source_generated_and_complete()
    {
        var path = WriteTemp(NntlTestData.Build().File);
        try
        {
            var (code, output, _) = await RunAsync("info", path, "--json");
            await Assert.That(code).IsEqualTo((int)ExitCode.Ok);

            using var json = JsonDocument.Parse(output);
            var root = json.RootElement;
            await Assert.That(root.GetProperty("indexSource").GetString()).IsEqualTo("Footer");
            await Assert.That(root.GetProperty("producer").GetString()).IsEqualTo(NntlTestData.Header.Producer);
            var sessions = root.GetProperty("sessions");
            await Assert.That(sessions.GetArrayLength()).IsEqualTo(3);
            var first = sessions[0];
            await Assert.That(first.GetProperty("id").GetInt64()).IsEqualTo(NntlTestData.SessionIds[0]);
            await Assert.That(first.GetProperty("role").GetString()).IsEqualTo("Server");
            await Assert.That(first.GetProperty("gaps").GetProperty("droppedBytes").GetInt64()).IsEqualTo(4_096);
            await Assert.That(first.GetProperty("close").GetProperty("reason").GetString()).IsEqualTo("Graceful");
            await Assert.That(first.GetProperty("inbound").GetProperty("ops").GetInt64()).IsGreaterThan(0);
            await Assert.That(first.TryGetProperty("messages", out _)).IsFalse();

            var deserialized = JsonSerializer.Deserialize(output, CliJsonContext.Default.RecordingSummary)!;
            await Assert.That(deserialized.Sessions.Length).IsEqualTo(3);
            await Assert.That(deserialized.Sessions[0].Gaps).IsEqualTo(new GapSummary(1, 17, 4_096));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task Info_reports_a_crashed_recording()
    {
        var file = NntlTestData.Build().File;
        var path = WriteTemp(file.AsSpan(0, (int)NntlTestData.IndexOffset(file) - 5).ToArray());
        try
        {
            var (code, output, _) = await RunAsync("info", path);
            await Assert.That(code).IsEqualTo((int)ExitCode.Ok);
            await Assert.That(output).Contains("rebuilt by scan");
            await Assert.That(output).Contains("torn tail");
            await Assert.That(output).Contains("Session " + NntlTestData.SessionIds[2]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task Info_of_a_file_that_is_not_a_recording_fails_with_1()
    {
        var path = WriteTemp("not a recording"u8.ToArray());
        try
        {
            var (code, _, error) = await RunAsync("info", path);
            await Assert.That(code).IsEqualTo((int)ExitCode.Failed);
            await Assert.That(error).Contains("cannot read");
            var missing = await RunAsync("info", path + ".missing");
            await Assert.That(missing.Code).IsEqualTo((int)ExitCode.Failed);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task Repair_restores_the_footer_of_a_truncated_file()
    {
        var file = NntlTestData.Build().File;
        var indexOffset = (int)NntlTestData.IndexOffset(file);
        // Lose the footer and tear the last block, as a crash mid-write would.
        var torn = file.AsSpan(0, indexOffset - 7).ToArray();
        var path = WriteTemp(torn);
        try
        {
            var (code, output, _) = await RunAsync("repair", path);
            await Assert.That(code).IsEqualTo((int)ExitCode.Ok);
            await Assert.That(output).Contains("rebuilt the index footer");
            await Assert.That(output).Contains("TornTail");

            using (var reader = NntlReader.Open(File.OpenRead(path)))
            {
                await Assert.That(reader.IndexSource).IsEqualTo(NntlIndexSource.Footer);
                await Assert.That(reader.Sessions.Count).IsEqualTo(3);
            }

            // Now intact: a second repair is a no-op.
            var repaired = await File.ReadAllBytesAsync(path);
            var again = await RunAsync("repair", path);
            await Assert.That(again.Code).IsEqualTo((int)ExitCode.Ok);
            await Assert.That(again.Output).Contains("nothing to repair");
            await Assert.That(await File.ReadAllBytesAsync(path)).IsEquivalentTo(repaired);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task Repair_leaves_an_intact_file_untouched_unless_forced()
    {
        var file = NntlTestData.Build().File;
        var path = WriteTemp(file);
        try
        {
            var written = File.GetLastWriteTimeUtc(path);
            var (code, output, _) = await RunAsync("repair", path);
            await Assert.That(code).IsEqualTo((int)ExitCode.Ok);
            await Assert.That(output).Contains("nothing to repair");
            await Assert.That(await File.ReadAllBytesAsync(path)).IsEquivalentTo(file);
            await Assert.That(File.GetLastWriteTimeUtc(path)).IsEqualTo(written);

            // Forced, the rebuilt footer of a cleanly closed file is byte-identical.
            var forced = await RunAsync("repair", path, "--force");
            await Assert.That(forced.Code).IsEqualTo((int)ExitCode.Ok);
            await Assert.That(forced.Output).Contains("rebuilt the index footer");
            await Assert.That(await File.ReadAllBytesAsync(path)).IsEquivalentTo(file);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
