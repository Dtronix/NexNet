using System.Globalization;
using System.Text;
using System.Text.Json;
using NexNet.Inspector.Core.Format;
using NexNet.Inspector.Sample;
using NexNet.Inspector.Tap;
using NexNet.Inspector.Tests.Session;
using NexNet.Inspector.Tests.Tap;

namespace NexNet.Inspector.Tests.Mcp;

/// <summary>
/// Every nexnet-tap mcp tool end to end: the file tools on the fixtures, and the live ones against an in-process tapped
/// server and its inspector endpoint.
/// </summary>
public class McpToolsTests
{
    private static readonly string Token = Encoding.UTF8.GetString(TapHarness.EndpointToken);

    private static string Fixture(FixtureKind kind) => Json(SessionFixtures.Path(kind));

    // A path as a JSON string body.
    private static string Json(string path) => JsonEncodedText.Encode(path).ToString();

    private static async Task<McpTestClient> StartAsync(string? token = null)
    {
        var client = new McpTestClient(token);
        await client.InitializeAsync();
        return client;
    }

    [Test]
    public async Task The_file_tools_print_what_the_queries_print()
    {
        await using var client = await StartAsync();
        var server = Fixture(FixtureKind.ChatServer);

        var summary = await client.CallAsync("summary", $$"""{"file":"{{server}}"}""");
        await Assert.That(summary.IsError).IsFalse();
        await Assert.That(summary.Text).Contains("calls 14");
        await Assert.That(summary.Text).Contains("END Graceful");

        var findings = await client.CallAsync("findings", $$"""{"file":"{{Fixture(FixtureKind.ServerAll)}}"}""");
        await Assert.That(findings.IsError).IsFalse();
        await Assert.That(findings.Text).Contains("@0x");
        var ndjson = await client.CallAsync("findings", $$"""{"file":"{{server}}","format":"ndjson","session":"first"}""");
        await Assert.That(ndjson.Text.Split('\n')[0]).StartsWith("{");

        var show = await client.CallAsync("show", $$"""{"file":"{{server}}","id":"#3"}""");
        await Assert.That(show.IsError).IsFalse();
        await Assert.That(show.Text).Contains("SendMessage");
        var bytes = await client.CallAsync("show", $$"""{"file":"{{server}}","id":"#3","bytes":true}""");
        await Assert.That(bytes.Text).Contains("@");
        var missing = await client.CallAsync("show", $$"""{"file":"{{server}}","id":"#999"}""");
        await Assert.That(missing.IsError).IsTrue();
        await Assert.That(missing.Text).Contains("no session holds #999");
        await Assert.That(missing.Text).Contains("(not found)");

        var trace = await client.CallAsync("trace", $$"""{"file":"{{server}}","id":"#4"}""");
        await Assert.That(trace.IsError).IsFalse();
        await Assert.That(trace.Text).Contains("SendAttachment");

        var around = await client.CallAsync("around", $$"""{"file":"{{server}}","t":100,"window":"±50","level":"frames"}""");
        await Assert.That(around.IsError).IsFalse();
        await Assert.That(around.Text.Split('\n').Length).IsGreaterThan(1);

        var diff = await client.CallAsync("diff", $$"""{"a":"{{Fixture(FixtureKind.ChatClient)}}","b":"{{Fixture(FixtureKind.ChatClientVariant)}}","top":2}""");
        await Assert.That(diff.IsError).IsFalse();
        await Assert.That(diff.Text).Contains("SendMessage only B");
    }

    [Test]
    public async Task Query_budgets_page_with_the_after_cursor()
    {
        await using var client = await StartAsync();
        var server = Fixture(FixtureKind.ChatServer);
        var first = await client.CallAsync("schema", $$"""{"file":"{{server}}","max_lines":4}""");
        var lines = first.Text.Split('\n');
        await Assert.That(lines.Length).IsEqualTo(5);
        await Assert.That(lines[^1]).StartsWith("… budget hit");
        var cursor = lines[^1][(lines[^1].IndexOf("--after ", StringComparison.Ordinal) + "--after ".Length)..];

        var second = await client.CallAsync("schema", $$"""{"file":"{{server}}","max_lines":4,"after":"{{cursor}}"}""");
        await Assert.That(second.IsError).IsFalse();
        await Assert.That(second.Text.Split('\n')[0]).IsNotEqualTo(lines[0]);

        // A cursor of another query is refused with the CLI's reason.
        var wrong = await client.CallAsync("summary", $$"""{"file":"{{server}}","after":"{{cursor}}"}""");
        await Assert.That(wrong.IsError).IsTrue();
        await Assert.That(wrong.Text).Contains("another query");
    }

    [Test]
    public async Task Export_section_writes_a_recording_that_opens_on_its_own()
    {
        await using var client = await StartAsync();
        var path = Path.Combine(Path.GetTempPath(), $"mcp-export-{Guid.NewGuid():N}.nntl");
        try
        {
            var export = await client.CallAsync("export_section",
                $$"""{"file":"{{Fixture(FixtureKind.ChatClient)}}","out":"{{Json(path)}}","from":"+20","to":60}""");
            await Assert.That(export.IsError).IsFalse();
            await Assert.That(export.Text).Contains("exported +20.0..+60.0");
            await Assert.That(File.Exists(path)).IsTrue();

            var again = await client.CallAsync("export_section", $$"""{"file":"{{Fixture(FixtureKind.ChatClient)}}","out":"{{Json(path)}}"}""");
            await Assert.That(again.IsError).IsTrue();
            await Assert.That(again.Text).Contains("force");

            var summary = await client.CallAsync("summary", $$"""{"file":"{{Json(path)}}"}""");
            await Assert.That(summary.IsError).IsFalse();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task Live_tools_list_attach_read_detach_and_query_a_tapped_server()
    {
        await using var h = new TapHarness(TapTransport.Tcp);
        await h.StartAsync();
        await h.StartEndpointAsync(TapTransport.Tcp);
        var app = await h.ConnectClientAsync();
        var session = await h.WaitForSessionAsync();
        await using var client = await StartAsync(Token);
        var endpoint = h.EndpointAddress!;

        var list = await client.CallAsync("list_sessions", $$"""{"endpoint":"{{endpoint}}"}""");
        await Assert.That(list.IsError).IsFalse();
        await Assert.That(list.Text).Contains(session.Id.ToString(CultureInfo.InvariantCulture));
        await Assert.That(list.Text).Contains("Recording disabled by server config");

        var attach = await client.CallAsync("attach", $$"""{"endpoint":"{{endpoint}}","session":{{session.Id}},"method":["Echo"]}""");
        await Assert.That(attach.IsError).IsFalse();
        await Assert.That(attach.Text).StartsWith("stream_id s1");
        await Assert.That(attach.Text).Contains("Tailing session");

        await app.Proxy.Echo([1]);
        await app.Proxy.Echo([2, 2]);
        await app.Proxy.Echo([3, 3, 3]);

        // Page one line at a time until the three calls arrived.
        var seen = new List<string>();
        long after = 0;
        for (var i = 0; i < 100 && seen.Count(l => l.Contains("Echo(", StringComparison.Ordinal)) < 3; i++)
        {
            var read = await client.CallAsync("read", $$"""{"stream_id":"s1","after":{{after}},"max_lines":1,"wait_ms":2000}""");
            await Assert.That(read.IsError).IsFalse();
            var lines = read.Text.Split('\n');
            var footer = lines[^1];
            await Assert.That(footer).StartsWith("… next: after=");
            await Assert.That(lines.Length).IsLessThanOrEqualTo(2);
            seen.AddRange(lines[..^1]);
            after = long.Parse(footer["… next: after=".Length..footer.IndexOf(' ', "… next: after=".Length)], CultureInfo.InvariantCulture);
        }

        await Assert.That(seen[0]).StartsWith("@0x");
        await Assert.That(seen.Count(l => l.Contains("Echo(", StringComparison.Ordinal))).IsEqualTo(3);
        await Assert.That(after).IsEqualTo((long)seen.Count);

        // A query of the live session (a short attach of its own).
        var summary = await client.CallAsync("summary", $$"""{"endpoint":"{{endpoint}}","session":"first","duration":"300ms"}""");
        await Assert.That(summary.IsError).IsFalse();
        await Assert.That(summary.Text).Contains("@0x");
        var schema = await client.CallAsync("schema", $$"""{"endpoint":"{{endpoint}}","duration":0.3}""");
        await Assert.That(schema.Text).Contains("Echo");

        var detach = await client.CallAsync("detach", """{"stream_id":"s1"}""");
        await Assert.That(detach.IsError).IsFalse();
        await Assert.That(detach.Text).Contains("detached s1");
        var gone = await client.CallAsync("read", """{"stream_id":"s1"}""");
        await Assert.That(gone.IsError).IsTrue();
        await Assert.That(gone.Text).Contains("No open stream 's1'");

        var unknown = await client.CallAsync("attach", $$"""{"endpoint":"{{endpoint}}","session":"424242"}""");
        await Assert.That(unknown.IsError).IsTrue();
        await Assert.That(unknown.Text).Contains("no live session 424242");

        await Assert.That(client.Output.ToString()).DoesNotContain(Token);
    }

    [Test]
    public async Task A_stream_that_ends_stays_readable_until_detached()
    {
        await using var h = new TapHarness(TapTransport.Tcp);
        await h.StartAsync();
        await h.StartEndpointAsync(TapTransport.Tcp);
        var app = await h.ConnectClientAsync();
        await h.WaitForSessionAsync();
        await using var client = await StartAsync(Token);

        var attach = await client.CallAsync("attach", $$"""{"endpoint":"{{h.EndpointAddress}}","session":"first","findings":true}""");
        await Assert.That(attach.IsError).IsFalse();
        await app.Proxy.Echo([9]);
        await app.DisconnectAsync();

        McpCallResult read = default;
        for (var i = 0; i < 50; i++)
        {
            read = await client.CallAsync("read", """{"stream_id":"s1","wait_ms":500}""");
            if (read.Text.Contains("stream ended", StringComparison.Ordinal))
                break;
            await Task.Delay(100);
        }

        await Assert.That(read.Text).Contains("END ");
        await Assert.That(read.Text).Contains("stream ended: ended normally");
        var detach = await client.CallAsync("detach", """{"stream_id":"s1"}""");
        await Assert.That(detach.Text).Contains("0 not read before detach");
    }

    [Test]
    public async Task Record_start_and_stop_write_a_recording_when_the_server_allows_it()
    {
        await using var h = new TapHarness(TapTransport.Tcp, new InspectorOptions { AllowRecording = true });
        await h.StartAsync();
        await h.StartEndpointAsync(TapTransport.Tcp);
        var app = await h.ConnectClientAsync();
        await h.WaitForSessionAsync();
        await using var client = await StartAsync(Token);
        var path = Path.Combine(Path.GetTempPath(), $"mcp-record-{Guid.NewGuid():N}.nntl");
        try
        {
            var start = await client.CallAsync("record_start", $$"""{"endpoint":"{{h.EndpointAddress}}","session":"first","out":"{{Json(path)}}"}""");
            await Assert.That(start.IsError).IsFalse();
            await Assert.That(start.Text).StartsWith("record_id r1");
            await Assert.That(start.Text).Contains("Recording session");
            await Assert.That(start.Text).DoesNotContain("Ctrl+C");

            await app.Proxy.Echo([4, 5, 6]);
            var stop = await client.CallAsync("record_stop", """{"record_id":"r1"}""");
            await Assert.That(stop.IsError).IsFalse();
            await Assert.That(stop.Text).Contains("r1 stopped");
            await Assert.That(stop.Text).Contains("Wrote ");
            using (var reader = NntlReader.Open(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)))
                await Assert.That(reader.IndexSource).IsEqualTo(NntlIndexSource.Footer);

            var again = await client.CallAsync("record_stop", """{"record_id":"r1"}""");
            await Assert.That(again.IsError).IsTrue();
            await Assert.That(again.Text).Contains("record_start returns a record_id");
            var findings = await client.CallAsync("findings", $$"""{"file":"{{Json(path)}}"}""");
            await Assert.That(findings.IsError).IsFalse();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task Recording_tools_respect_the_server_recording_policy()
    {
        await using var h = new TapHarness(TapTransport.Tcp);
        await h.StartAsync();
        await h.StartEndpointAsync(TapTransport.Tcp);
        await h.ConnectClientAsync();
        await h.WaitForSessionAsync();
        await using var client = await StartAsync(Token);
        var path = Path.Combine(Path.GetTempPath(), $"mcp-refused-{Guid.NewGuid():N}.nntl");

        var record = await client.CallAsync("record_start", $$"""{"endpoint":"{{h.EndpointAddress}}","session":"first","out":"{{Json(path)}}"}""");
        await Assert.That(record.IsError).IsTrue();
        await Assert.That(record.Text).Contains("Recording disabled by server config");
        await Assert.That(record.Text).Contains("(refused by the server configuration)");

        var export = await client.CallAsync("export_section", $$"""{"endpoint":"{{h.EndpointAddress}}","out":"{{Json(path)}}"}""");
        await Assert.That(export.IsError).IsTrue();
        await Assert.That(export.Text).Contains("Recording disabled by server config");

        var all = await client.CallAsync("attach", $$"""{"endpoint":"{{h.EndpointAddress}}","session":"all"}""");
        await Assert.That(all.IsError).IsTrue();
        await Assert.That(all.Text).Contains("Recording disabled by server config");
        await Assert.That(File.Exists(path)).IsFalse();

        // A wrong token is refused with the endpoint's reason, and not echoed.
        var wrong = await client.CallAsync("list_sessions", $$"""{"endpoint":"{{h.EndpointAddress}}","token":"wrong-token-123"}""");
        await Assert.That(wrong.IsError).IsTrue();
        await Assert.That(wrong.Text).Contains("refused the token");
        await Assert.That(client.Output.ToString()).DoesNotContain("wrong-token-123");
    }

    [Test]
    public async Task Closing_stdin_stops_open_streams_and_recordings()
    {
        await using var h = new TapHarness(TapTransport.Tcp, new InspectorOptions { AllowRecording = true });
        await h.StartAsync();
        await h.StartEndpointAsync(TapTransport.Tcp);
        await h.ConnectClientAsync();
        await h.WaitForSessionAsync();
        var path = Path.Combine(Path.GetTempPath(), $"mcp-close-{Guid.NewGuid():N}.nntl");
        try
        {
            var client = await StartAsync(Token);
            await Assert.That((await client.CallAsync("attach", $$"""{"endpoint":"{{h.EndpointAddress}}","session":"first"}""")).IsError).IsFalse();
            await Assert.That((await client.CallAsync("record_start", $$"""{"endpoint":"{{h.EndpointAddress}}","session":"first","out":"{{Json(path)}}"}""")).IsError)
                .IsFalse();
            client.Close();
            await client.Run.WaitAsync(TimeSpan.FromSeconds(60));
            await client.DisposeAsync();

            // The recording was stopped as Ctrl+C stops it: with its footer.
            using var reader = NntlReader.Open(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
            await Assert.That(reader.IndexSource).IsEqualTo(NntlIndexSource.Footer);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
