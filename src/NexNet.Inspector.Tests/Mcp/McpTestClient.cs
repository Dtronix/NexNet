using System.Text.Json;
using System.Threading.Channels;
using NexNet.Inspector.Cli;

namespace NexNet.Inspector.Tests.Mcp;

/// <summary>A tool call's result as the client sees it.</summary>
internal readonly record struct McpCallResult(string Text, bool IsError, JsonElement Result);

/// <summary>
/// An in-process MCP client: runs <see cref="McpServer"/> over an in-memory stdin (a channel of lines) and stdout
/// (a <see cref="McpLineBuffer"/>), and matches responses to requests by id.
/// </summary>
internal sealed class McpTestClient : IAsyncDisposable
{
    public static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    private readonly LineInput _input = new();
    private int _id;

    public McpTestClient(string? defaultToken = null)
    {
        Server = new McpServer(_input, Output, Log, defaultToken);
        Run = Task.Run(() => Server.RunAsync(CancellationToken.None));
    }

    public McpServer Server { get; }

    /// <summary>The server's stdout, one message per line.</summary>
    public McpLineBuffer Output { get; } = new(100_000);

    /// <summary>The server's stderr.</summary>
    public McpLineBuffer Log { get; } = new();

    public Task Run { get; }

    public void Send(string line) => _input.Push(line);

    /// <summary>Closes stdin.</summary>
    public void Close() => _input.End();

    public int NextId() => Interlocked.Increment(ref _id);

    public async Task<JsonElement> RequestAsync(string method, string? parameters = null)
    {
        var id = NextId();
        Send(Request(id, method, parameters));
        return await ResponseAsync(id);
    }

    public static string Request(int id, string method, string? parameters) =>
        $$"""{"jsonrpc":"2.0","id":{{id}},"method":"{{method}}"{{(parameters is null ? "" : ",\"params\":" + parameters)}}}""";

    public Task InitializeAsync() => InitializeCoreAsync();

    private async Task InitializeCoreAsync()
    {
        await RequestAsync("initialize", """{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"test","version":"1"}}""");
        Send("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");
    }

    public async Task<McpCallResult> CallAsync(string tool, string arguments = "{}")
    {
        var response = await RequestAsync("tools/call", $$"""{"name":"{{tool}}","arguments":{{arguments}}}""");
        if (!response.TryGetProperty("result", out var result))
            throw new InvalidOperationException("tools/call failed: " + response.GetRawText());
        var text = result.GetProperty("content")[0].GetProperty("text").GetString()!;
        return new McpCallResult(text, result.GetProperty("isError").GetBoolean(), result);
    }

    /// <summary>The response with this id.</summary>
    public async Task<JsonElement> ResponseAsync(int id, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? Wait);
        while (true)
        {
            var changed = Output.Changed;
            foreach (var message in Messages())
            {
                if (message.TryGetProperty("id", out var found) && found.ValueKind == JsonValueKind.Number && found.GetInt32() == id)
                    return message;
            }

            var left = deadline - DateTime.UtcNow;
            if (left <= TimeSpan.Zero)
                throw new TimeoutException($"No response to request {id}. Output:\n{Output}\nLog:\n{Log}");
            await Task.WhenAny(changed, Task.Delay(left));
        }
    }

    /// <summary>Every message written so far, parsed (each line must be one JSON value).</summary>
    public List<JsonElement> Messages()
    {
        var lines = new List<string>();
        Output.Read(0, int.MaxValue, lines, out _);
        var messages = new List<JsonElement>(lines.Count);
        foreach (var line in lines)
        {
            using var document = JsonDocument.Parse(line);
            messages.Add(document.RootElement.Clone());
        }

        return messages;
    }

    public async ValueTask DisposeAsync()
    {
        Close();
        await Run.WaitAsync(TimeSpan.FromSeconds(90));
        await Server.DisposeAsync();
    }

    /// <summary>stdin: lines pushed by the test; null ends it.</summary>
    private sealed class LineInput : TextReader
    {
        private readonly Channel<string> _lines = Channel.CreateUnbounded<string>();

        public void Push(string line) => _lines.Writer.TryWrite(line);

        public void End() => _lines.Writer.TryComplete();

        public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            try
            {
                return await _lines.Reader.ReadAsync(cancellationToken);
            }
            catch (ChannelClosedException)
            {
                return null;
            }
        }

        public override string? ReadLine() => ReadLineAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();
    }
}
