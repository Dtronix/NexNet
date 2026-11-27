using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using NexNet.Inspector.Cli;
using NexNet.Inspector.Sample;
using NexNet.Inspector.Tests.Session;

namespace NexNet.Inspector.Tests.Mcp;

/// <summary>JSON-RPC 2.0 and MCP conformance of nexnet-tap mcp: framing, errors, lifecycle, versions, tools/list, cancellation.</summary>
public class McpProtocolTests
{
    private const string ModernMeta = """
        "_meta":{"io.modelcontextprotocol/protocolVersion":"2026-07-28","io.modelcontextprotocol/clientCapabilities":{}}
        """;

    private static int Code(JsonElement response) => response.GetProperty("error").GetProperty("code").GetInt32();

    [Test]
    public async Task Initialize_negotiates_the_version_and_declares_tools_only()
    {
        await using var client = new McpTestClient();
        var response = await client.RequestAsync("initialize", """{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"t","version":"1"}}""");
        var result = response.GetProperty("result");
        await Assert.That(response.GetProperty("jsonrpc").GetString()).IsEqualTo("2.0");
        await Assert.That(result.GetProperty("protocolVersion").GetString()).IsEqualTo("2025-06-18");
        var capabilities = result.GetProperty("capabilities");
        await Assert.That(capabilities.EnumerateObject().Select(p => p.Name).ToArray()).IsEquivalentTo(new[] { "tools" });
        await Assert.That(result.GetProperty("serverInfo").GetProperty("name").GetString()).IsEqualTo("nexnet-tap");
        await Assert.That(result.GetProperty("instructions").GetString()).Contains("Text format legend");

        // A revision the server does not serve: it answers with the newest legacy one.
        var other = await client.RequestAsync("initialize", """{"protocolVersion":"1999-01-01","capabilities":{},"clientInfo":{"name":"t","version":"1"}}""");
        await Assert.That(other.GetProperty("result").GetProperty("protocolVersion").GetString()).IsEqualTo(McpServer.LegacyProtocolVersion);
    }

    [Test]
    public async Task Ping_answers_an_empty_result_before_and_after_initialize()
    {
        await using var client = new McpTestClient();
        var before = await client.RequestAsync("ping");
        await Assert.That(before.GetProperty("result").ValueKind).IsEqualTo(JsonValueKind.Object);
        await Assert.That(before.GetProperty("result").EnumerateObject().Count()).IsEqualTo(0);
        await client.InitializeAsync();
        var after = await client.RequestAsync("ping");
        await Assert.That(after.GetProperty("result").EnumerateObject().Count()).IsEqualTo(0);
    }

    [Test]
    public async Task Lines_that_are_not_requests_get_the_JSON_RPC_errors()
    {
        await using var client = new McpTestClient();
        client.Send("not json");
        client.Send("[{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\"}]");
        client.Send("42");
        client.Send("""{"jsonrpc":"1.0","id":7,"method":"ping"}""");
        client.Send("""{"jsonrpc":"2.0","id":{"x":1},"method":"ping"}""");
        client.Send("""{"jsonrpc":"2.0","id":8}""");
        client.Send("""{"jsonrpc":"2.0","id":9,"method":"ping","params":[1]}""");
        // A response (to nothing this server asked): ignored.
        client.Send("""{"jsonrpc":"2.0","id":10,"result":{}}""");
        await client.ResponseAsync(9);
        var last = await client.RequestAsync("ping");
        await Assert.That(last.TryGetProperty("result", out _)).IsTrue();

        var messages = client.Messages();
        await Assert.That(messages.Count).IsEqualTo(8);
        await Assert.That(Code(messages[0])).IsEqualTo(McpServer.ParseError);
        await Assert.That(messages[0].GetProperty("id").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(Code(messages[1])).IsEqualTo(McpServer.InvalidRequest);
        await Assert.That(messages[1].GetProperty("error").GetProperty("message").GetString()).Contains("batches");
        await Assert.That(Code(messages[2])).IsEqualTo(McpServer.InvalidRequest);
        await Assert.That(Code(messages[3])).IsEqualTo(McpServer.InvalidRequest);
        await Assert.That(messages[3].GetProperty("id").GetInt32()).IsEqualTo(7);
        await Assert.That(Code(messages[4])).IsEqualTo(McpServer.InvalidRequest);
        await Assert.That(messages[4].GetProperty("id").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(Code(messages[5])).IsEqualTo(McpServer.InvalidRequest);
        await Assert.That(messages[5].GetProperty("id").GetInt32()).IsEqualTo(8);
        await Assert.That(Code(messages[6])).IsEqualTo(McpServer.InvalidParams);
    }

    [Test]
    public async Task Unknown_methods_fail_and_notifications_get_no_response()
    {
        await using var client = new McpTestClient();
        var unknown = await client.RequestAsync("resources/list", "{}");
        await Assert.That(Code(unknown)).IsEqualTo(McpServer.MethodNotFound);
        await Assert.That(unknown.GetProperty("error").GetProperty("message").GetString()).Contains("resources/list");

        // String ids are echoed as strings.
        client.Send("""{"jsonrpc":"2.0","id":"abc","method":"ping"}""");
        client.Send("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");
        client.Send("""{"jsonrpc":"2.0","method":"notifications/whatever","params":{}}""");
        client.Send("""{"jsonrpc":"2.0","method":"no/such/method"}""");
        await client.RequestAsync("ping");
        var messages = client.Messages();
        await Assert.That(messages.Count).IsEqualTo(3);
        await Assert.That(messages[1].GetProperty("id").GetString()).IsEqualTo("abc");
    }

    [Test]
    public async Task Without_initialize_a_request_must_name_the_current_revision()
    {
        await using var client = new McpTestClient();
        var missing = await client.RequestAsync("tools/list", "{}");
        await Assert.That(Code(missing)).IsEqualTo(McpServer.InvalidParams);
        await Assert.That(missing.GetProperty("error").GetProperty("message").GetString()).Contains("initialize");

        var old = await client.RequestAsync("tools/list",
            """{"_meta":{"io.modelcontextprotocol/protocolVersion":"2024-11-05","io.modelcontextprotocol/clientCapabilities":{}}}""");
        await Assert.That(Code(old)).IsEqualTo(McpServer.UnsupportedProtocolVersion);
        var data = old.GetProperty("error").GetProperty("data");
        await Assert.That(data.GetProperty("supported")[0].GetString()).IsEqualTo(McpServer.ProtocolVersion);
        await Assert.That(data.GetProperty("requested").GetString()).IsEqualTo("2024-11-05");

        var noCapabilities = await client.RequestAsync("tools/list", """{"_meta":{"io.modelcontextprotocol/protocolVersion":"2026-07-28"}}""");
        await Assert.That(Code(noCapabilities)).IsEqualTo(McpServer.InvalidParams);

        var modern = await client.RequestAsync("tools/list", "{" + ModernMeta + "}");
        var result = modern.GetProperty("result");
        await Assert.That(result.GetProperty("resultType").GetString()).IsEqualTo("complete");
        await Assert.That(result.GetProperty("cacheScope").GetString()).IsEqualTo("public");
        await Assert.That(result.GetProperty("_meta").GetProperty("io.modelcontextprotocol/serverInfo").GetProperty("name").GetString())
            .IsEqualTo("nexnet-tap");

        var discover = await client.RequestAsync("server/discover", "{" + ModernMeta + "}");
        var found = discover.GetProperty("result");
        await Assert.That(found.GetProperty("resultType").GetString()).IsEqualTo("complete");
        await Assert.That(found.GetProperty("supportedVersions").EnumerateArray().Select(v => v.GetString()!).ToArray())
            .IsEquivalentTo(McpServer.SupportedVersions);
        await Assert.That(found.GetProperty("capabilities").TryGetProperty("tools", out _)).IsTrue();
        await Assert.That(found.GetProperty("ttlMs").GetInt64()).IsGreaterThan(0);

        // A modern tools/call result carries resultType too.
        var call = await client.RequestAsync("tools/call", "{\"name\":\"read\",\"arguments\":{\"stream_id\":\"s9\"}," + ModernMeta + "}");
        await Assert.That(call.GetProperty("result").GetProperty("resultType").GetString()).IsEqualTo("complete");
        await Assert.That(call.GetProperty("result").GetProperty("isError").GetBoolean()).IsTrue();
    }

    [Test]
    public async Task Tools_list_describes_every_tool_with_an_object_schema()
    {
        await using var client = new McpTestClient();
        await client.InitializeAsync();
        var tools = (await client.RequestAsync("tools/list")).GetProperty("result").GetProperty("tools").EnumerateArray().ToArray();
        await Assert.That(tools.Length).IsEqualTo(Enum.GetValues<McpToolKind>().Length);
        string[] expected = ["list_sessions", "attach", "read", "detach", "summary", "findings", "show", "trace", "around", "schema", "diff",
            "export_section", "record_start", "record_stop"];
        await Assert.That(tools.Select(t => t.GetProperty("name").GetString()!).ToArray()).IsEquivalentTo(expected);
        for (var i = 0; i < tools.Length; i++)
        {
            var tool = tools[i];
            await Assert.That(McpTools.All[i].Kind).IsEqualTo((McpToolKind)i);
            await Assert.That(tool.GetProperty("name").GetString()).IsEqualTo(expected[i]);
            await Assert.That(tool.GetProperty("description").GetString()!.Length).IsGreaterThan(40);
            var schema = tool.GetProperty("inputSchema");
            await Assert.That(schema.GetProperty("type").GetString()).IsEqualTo("object");
            await Assert.That(schema.GetProperty("additionalProperties").GetBoolean()).IsFalse();
            var properties = schema.GetProperty("properties");
            foreach (var property in properties.EnumerateObject())
            {
                await Assert.That(property.Value.TryGetProperty("type", out _)).IsTrue().Because(expected[i] + "." + property.Name);
                await Assert.That(property.Value.TryGetProperty("description", out _)).IsTrue().Because(expected[i] + "." + property.Name);
            }

            if (schema.TryGetProperty("required", out var required))
            {
                foreach (var name in required.EnumerateArray())
                    await Assert.That(properties.TryGetProperty(name.GetString()!, out _)).IsTrue().Because(expected[i]);
            }

            await Assert.That(tool.GetProperty("annotations").TryGetProperty("readOnlyHint", out _)).IsTrue();
        }
    }

    [Test]
    public async Task Tool_calls_report_bad_names_as_errors_and_bad_arguments_as_tool_errors()
    {
        await using var client = new McpTestClient();
        await client.InitializeAsync();
        var unknown = await client.RequestAsync("tools/call", """{"name":"nope","arguments":{}}""");
        await Assert.That(Code(unknown)).IsEqualTo(McpServer.InvalidParams);
        await Assert.That(unknown.GetProperty("error").GetProperty("message").GetString()).Contains("Unknown tool: nope");
        var nameless = await client.RequestAsync("tools/call", """{"arguments":{}}""");
        await Assert.That(Code(nameless)).IsEqualTo(McpServer.InvalidParams);

        var file = SessionFixtures.Path(FixtureKind.ChatServer).Replace("\\", "/", StringComparison.Ordinal);
        (string Args, string Expect)[] cases =
        [
            ("{}", "Missing required argument 'file'"),
            ($$"""{"file":"{{file}}","bogus":1}""", "Unknown argument 'bogus'"),
            ("""{"file":5}""", "Argument 'file' must be a string, not a number"),
            ($$"""{"file":"{{file}}","format":"xml"}""", "must be one of text, ndjson"),
            ($$"""{"file":"{{file}}","slow":true}""", "must be a string or a number"),
        ];
        foreach (var (args, expect) in cases)
        {
            var result = await client.CallAsync("findings", args);
            await Assert.That(result.IsError).IsTrue().Because(args);
            await Assert.That(result.Text).Contains(expect).Because(args);
        }

        var range = await client.CallAsync("read", """{"stream_id":"s1","max_lines":5000}""");
        await Assert.That(range.IsError).IsTrue();
        await Assert.That(range.Text).Contains("at most 1000");
        var both = await client.CallAsync("summary", $$"""{"file":"{{file}}","endpoint":"tcp://127.0.0.1:1"}""");
        await Assert.That(both.Text).Contains("not both");
        var neither = await client.CallAsync("summary");
        await Assert.That(neither.IsError).IsTrue();
        await Assert.That(neither.Text).Contains("Give file");
        var missingFile = await client.CallAsync("summary", """{"file":"no-such-file.nntl"}""");
        await Assert.That(missingFile.IsError).IsTrue();
        await Assert.That(missingFile.Text).Contains("no file at");
    }

    [Test]
    public async Task Every_message_is_one_line_with_no_embedded_newline()
    {
        await using var client = new McpTestClient();
        await client.InitializeAsync();
        await client.RequestAsync("tools/list");
        var summary = await client.CallAsync("summary", $$"""{"file":"{{SessionFixtures.Path(FixtureKind.ChatServer).Replace("\\", "/", StringComparison.Ordinal)}}"}""");
        await Assert.That(summary.IsError).IsFalse();
        await Assert.That(summary.Text).Contains("\n");
        var raw = client.Output.ToString();
        var lines = raw.Split('\n');
        await Assert.That(lines.Length).IsEqualTo(3);
        foreach (var line in lines)
        {
            await Assert.That(line).DoesNotContain("\r");
            using var document = JsonDocument.Parse(line);
            await Assert.That(document.RootElement.GetProperty("jsonrpc").GetString()).IsEqualTo("2.0");
        }

        // Logs go to stderr only.
        await Assert.That(client.Log.ToString()).Contains("initialize");
    }

    [Test]
    public async Task A_cancelled_call_gets_no_response_and_the_server_goes_on()
    {
        // An endpoint that accepts and never answers: the attach waits for the handshake.
        using var silent = new TcpListener(IPAddress.Loopback, 0);
        silent.Start();
        var port = ((IPEndPoint)silent.LocalEndpoint).Port;
        await using var client = new McpTestClient("t0ken");
        await client.InitializeAsync();
        var id = client.NextId();
        client.Send(McpTestClient.Request(id, "tools/call", $$$"""{"name":"attach","arguments":{"endpoint":"tcp://127.0.0.1:{{{port}}}","session":"first"}}"""));
        for (var i = 0; i < 500 && client.Server.InFlight == 0; i++)
            await Task.Delay(10);
        await Assert.That(client.Server.InFlight).IsEqualTo(1);

        client.Send($$$"""{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":{{{id}}},"reason":"test"}}""");
        for (var i = 0; i < 3000 && client.Server.InFlight > 0; i++)
            await Task.Delay(10);
        await Assert.That(client.Server.InFlight).IsEqualTo(0);
        var ping = await client.RequestAsync("ping");
        await Assert.That(ping.TryGetProperty("result", out _)).IsTrue();
        await Assert.That(client.Messages().Any(m => m.TryGetProperty("id", out var x) && x.ValueKind == JsonValueKind.Number && x.GetInt32() == id))
            .IsFalse();
        // Cancelling an unknown or finished request is ignored.
        client.Send("""{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":12345}}""");
        await Assert.That((await client.RequestAsync("ping")).TryGetProperty("result", out _)).IsTrue();
    }

    [Test]
    public async Task Closing_stdin_answers_calls_in_flight_then_ends()
    {
        var client = new McpTestClient();
        await client.InitializeAsync();
        var id = client.NextId();
        client.Send(McpTestClient.Request(id, "tools/call",
            $$$"""{"name":"summary","arguments":{"file":"{{{SessionFixtures.Path(FixtureKind.ServerAll).Replace("\\", "/", StringComparison.Ordinal)}}}"}}"""));
        client.Close();
        await client.Run.WaitAsync(McpTestClient.Wait);
        var response = await client.ResponseAsync(id, TimeSpan.FromSeconds(1));
        await Assert.That(response.GetProperty("result").GetProperty("isError").GetBoolean()).IsFalse();
        await client.DisposeAsync();
    }

    [Test]
    public async Task Tokens_are_never_echoed_and_a_missing_token_says_where_to_put_one()
    {
        const string secret = "super-secret-token-7f3a";
        await using (var client = new McpTestClient())
        {
            await client.InitializeAsync();
            var none = await client.CallAsync("list_sessions", """{"endpoint":"tcp://127.0.0.1:1"}""");
            await Assert.That(none.IsError).IsTrue();
            await Assert.That(none.Text).Contains("NEXNET_TAP_TOKEN");

            var refused = await client.CallAsync("list_sessions", $$"""{"endpoint":"tcp://127.0.0.1:1","token":"{{secret}}"}""");
            await Assert.That(refused.IsError).IsTrue();
            await Assert.That(refused.Text).Contains("could not connect");
            await Assert.That(client.Output.ToString()).DoesNotContain(secret);
            await Assert.That(client.Log.ToString()).DoesNotContain(secret);
        }

        // The server's default token (NEXNET_TAP_TOKEN or mcp --token) is used and not echoed either.
        await using var withDefault = new McpTestClient(secret);
        await withDefault.InitializeAsync();
        var used = await withDefault.CallAsync("list_sessions", """{"endpoint":"tcp://127.0.0.1:1"}""");
        await Assert.That(used.Text).Contains("could not connect");
        await Assert.That(withDefault.Output.ToString()).DoesNotContain(secret);
    }

    [Test]
    public async Task The_mcp_command_takes_its_token_from_the_environment_or_the_command_line()
    {
        await Assert.That(CliArguments.TryParse(["mcp"], "from-env", out var env, out _)).IsTrue();
        await Assert.That(env!.Token).IsEqualTo("from-env");
        await Assert.That(CliArguments.TryParse(["mcp", "--token", "given"], "from-env", out var given, out _)).IsTrue();
        await Assert.That(given!.Token).IsEqualTo("given");
        await Assert.That(CliArguments.TryParse(["mcp", "--endpoint", "tcp://h:1"], null, out _, out var error)).IsFalse();
        await Assert.That(error).Contains("does not apply to 'mcp'");
    }

    [Test]
    public async Task The_line_buffer_pages_by_line_number_and_drops_the_oldest_lines()
    {
        var buffer = new McpLineBuffer(capacity: 20);
        buffer.Write("a\r\nb\nc");
        var lines = new List<string>();
        await Assert.That(buffer.Read(0, 10, lines, out var dropped)).IsEqualTo(2L);
        await Assert.That(lines).IsEquivalentTo(new[] { "a", "b" });
        await Assert.That(dropped).IsEqualTo(0L);
        buffer.WriteLine();
        lines.Clear();
        await Assert.That(buffer.Read(2, 10, lines, out _)).IsEqualTo(3L);
        await Assert.That(lines).IsEquivalentTo(new[] { "c" });

        for (var i = 0; i < 40; i++)
            buffer.WriteLine("line " + i);
        await Assert.That(buffer.Count).IsEqualTo(43L);
        lines.Clear();
        var next = buffer.Read(0, 5, lines, out dropped);
        await Assert.That(dropped).IsGreaterThan(0L);
        await Assert.That(next).IsEqualTo(dropped + 5);
        await Assert.That(lines[^1]).IsEqualTo("line " + (next - 4));
        lines.Clear();
        await Assert.That(buffer.Read(43, 5, lines, out _)).IsEqualTo(43L);
        await Assert.That(lines.Count).IsEqualTo(0);
    }
}
