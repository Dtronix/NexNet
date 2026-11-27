using System.Diagnostics;
using System.Text;
using System.Text.Json;
using NexNet.Inspector.Sample;
using NexNet.Inspector.Tests.Session;

namespace NexNet.Inspector.Tests.Mcp;

/// <summary>nexnet-tap mcp as a client launches it: a child process on stdin/stdout.</summary>
public class McpProcessTests
{
    [Test]
    public async Task The_spawned_server_speaks_one_JSON_RPC_message_per_line_on_stdio()
    {
        var apphost = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "nexnet-tap.exe" : "nexnet-tap");
        var start = File.Exists(apphost)
            ? new ProcessStartInfo(apphost)
            : new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
            {
                ArgumentList = { Path.Combine(AppContext.BaseDirectory, "nexnet-tap.dll") },
            };
        start.ArgumentList.Add("mcp");
        start.UseShellExecute = false;
        start.RedirectStandardInput = true;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        var utf8 = new UTF8Encoding(false);
        start.StandardInputEncoding = utf8;
        start.StandardOutputEncoding = utf8;
        start.StandardErrorEncoding = utf8;
        start.Environment["NEXNET_TAP_TOKEN"] = "env-token-from-process";

        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        var file = JsonEncodedText.Encode(SessionFixtures.Path(FixtureKind.ChatServer)).ToString();
        string[] requests =
        [
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"smoke","version":"1"}}}""",
            """{"jsonrpc":"2.0","method":"notifications/initialized"}""",
            """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""",
            "{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/call\",\"params\":{\"name\":\"summary\",\"arguments\":{\"file\":\"" + file + "\"}}}",
            // The default token comes from the environment: the call gets as far as connecting.
            """{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"list_sessions","arguments":{"endpoint":"tcp://127.0.0.1:1"}}}""",
            """{"jsonrpc":"2.0","id":5,"method":"ping"}""",
        ];
        foreach (var request in requests)
            await process.StandardInput.WriteAsync(request + "\n");
        await process.StandardInput.FlushAsync();
        process.StandardInput.Close();

        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
        await Assert.That(process.ExitCode).IsEqualTo(0);
        var output = await stdout;
        await Assert.That(output).EndsWith("\n");
        await Assert.That(output).DoesNotContain("\r");
        var lines = output.TrimEnd('\n').Split('\n');
        await Assert.That(lines.Length).IsEqualTo(5);
        var byId = new Dictionary<int, JsonElement>();
        foreach (var line in lines)
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement.Clone();
            byId[root.GetProperty("id").GetInt32()] = root;
        }

        await Assert.That(byId[1].GetProperty("result").GetProperty("protocolVersion").GetString()).IsEqualTo("2025-11-25");
        await Assert.That(byId[2].GetProperty("result").GetProperty("tools").GetArrayLength()).IsEqualTo(14);
        var summary = byId[3].GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;
        // The text format's glyphs survive the UTF-8 round trip.
        await Assert.That(summary).Contains("≋");
        await Assert.That(summary).Contains("☰");
        var list = byId[4].GetProperty("result");
        await Assert.That(list.GetProperty("isError").GetBoolean()).IsTrue();
        await Assert.That(list.GetProperty("content")[0].GetProperty("text").GetString()).Contains("could not connect");
        await Assert.That(output).DoesNotContain("env-token-from-process");
        await Assert.That(byId[5].GetProperty("result").EnumerateObject().Count()).IsEqualTo(0);
        await Assert.That(await stderr).Contains("serving MCP");
    }
}
