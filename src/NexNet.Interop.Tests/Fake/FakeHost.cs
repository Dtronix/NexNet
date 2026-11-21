using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using NexNet.Interop.Remote;

namespace NexNet.Interop.Tests.Fake;

/// <summary>What a <see cref="FakeHost"/> says in its hello, and how it misbehaves.</summary>
internal sealed record FakeHostOptions
{
    public string Language { get; init; } = "fake";

    public int Proto { get; init; } = HostHello.ProtocolVersion;

    /// <summary>Overrides the token of the hello (null: the launch token).</summary>
    public string? Token { get; init; }

    public string SchemaHash { get; init; } = "";

    public int MaxConcurrency { get; init; } = 4;

    public string[] Capabilities { get; init; } = ["Tcp", "Uds", "HttpSocket", "CustomCa", "FakeTime", "ClientPool"];

    public string[] Transports { get; init; } = ClientTransports.All.Select(t => t.ToString()).ToArray();

    public bool AnswerPings { get; init; } = true;
}

/// <summary>Thrown for an unknown or foreign handle (error kind UnknownHandle).</summary>
internal sealed class UnknownHandleException(string message) : Exception(message);

/// <summary>
/// A language host written in C#, for the protocol tests: it speaks the control protocol (hello, requests, events) and
/// drives the C# client and client pool in process (<see cref="FakeClient"/>, <see cref="FakePool"/>). Extra ops for the tests: <c>fake.hang</c> never answers,
/// <c>fake.sleep{ms}</c> answers late.
/// </summary>
internal sealed class FakeHost : IAsyncDisposable
{
    private readonly TcpClient _tcp;
    private readonly NetworkStream _stream;
    private readonly Channel<byte[]> _outbound = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    private readonly ConcurrentDictionary<string, FakeScope> _scopes = new(StringComparer.Ordinal);
    private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly CancellationTokenSource _closing = new();
    private int _nextHandle;
    private long _nextCallId;

    private FakeHost(TcpClient tcp, FakeHostOptions options)
    {
        _tcp = tcp;
        _stream = tcp.GetStream();
        Options = options;
        AnswerPings = options.AnswerPings;
    }

    public FakeHostOptions Options { get; }

    /// <summary>False: pings are never answered (a hung host).</summary>
    public volatile bool AnswerPings;

    /// <summary>The ops received, in order.</summary>
    public ConcurrentQueue<string> Ops { get; } = new();

    /// <summary>Completes with the "exit code" when the host stops.</summary>
    public Task<int> Exited => _exited.Task;

    public long HostNs => (long)(_clock.Elapsed.TotalMilliseconds * 1_000_000);

    /// <summary>Connects to <paramref name="control"/> (<c>127.0.0.1:port</c>) and sends the hello.</summary>
    public static async Task<FakeHost> ConnectAsync(string control, string token, FakeHostOptions options)
    {
        var parts = control.Split(':');
        var tcp = new TcpClient { NoDelay = true };
        await tcp.ConnectAsync(parts[0], int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture));
        var host = new FakeHost(tcp, options);
        var hello = new HostHello
        {
            Proto = options.Proto,
            Token = options.Token ?? token,
            Lang = options.Language,
            Runtime = ".NET " + Environment.Version,
            Lib = "fake host",
            SchemaHash = options.SchemaHash,
            CapabilityNames = options.Capabilities,
            TransportNames = options.Transports,
            MaxConcurrency = options.MaxConcurrency,
            Pid = Environment.ProcessId,
        };
        await ControlFraming.WriteAsync(host._stream, ControlFrameKind.Json, Encoding.UTF8.GetBytes(hello.ToJson().ToJsonString()));
        _ = Task.Run(host.WriteLoopAsync);
        _ = Task.Run(host.ReadLoopAsync);
        return host;
    }

    /// <summary>Writes raw bytes on the control stream (protocol violations).</summary>
    public Task WriteRawAsync(byte[] bytes) => _stream.WriteAsync(bytes).AsTask();

    /// <summary>Drops the connection abruptly, as a crashed process does.</summary>
    public void Crash(int exitCode = -1)
    {
        try
        {
            _tcp.Client.LingerState = new LingerOption(true, 0);
            _tcp.Close();
        }
        catch (Exception)
        {
            // Already closed.
        }

        _closing.Cancel();
        _exited.TrySetResult(exitCode);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var scope in _scopes.Values)
            await scope.DisposeAsync();
        Crash(0);
    }

    /// <summary>Sends an event; queued in call order.</summary>
    public void SendEvent(string? test, string? handle, string name, JsonObject data)
    {
        var evt = new JsonObject { ["t"] = "evt", ["test"] = test, ["h"] = handle, ["ev"] = name, ["data"] = data, ["hostNs"] = HostNs };
        _outbound.Writer.TryWrite(Encoding.UTF8.GetBytes(evt.ToJsonString()));
    }

    public string NewHandle(string prefix) => prefix + Interlocked.Increment(ref _nextHandle);

    public long NewCallId() => Interlocked.Increment(ref _nextCallId);

    private async Task WriteLoopAsync()
    {
        try
        {
            await foreach (var payload in _outbound.Reader.ReadAllAsync(_closing.Token))
                await ControlFraming.WriteAsync(_stream, ControlFrameKind.Json, payload, _closing.Token);
        }
        catch (Exception)
        {
            Crash();
        }
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (true)
            {
                var frame = await ControlFraming.ReadAsync(_stream, _closing.Token);
                if (frame == null)
                    break;
                var message = (JsonObject)JsonNode.Parse(frame.Value.Payload)!;
                if (message["t"]?.GetValue<string>() == "bye")
                    break;
                _ = HandleAsync(message);
            }
        }
        catch (Exception)
        {
            // Closed.
        }

        await DisposeScopesAsync();
        Crash(0);
    }

    private async Task DisposeScopesAsync()
    {
        foreach (var scope in _scopes.Values)
        {
            try
            {
                await scope.DisposeAsync();
            }
            catch (Exception)
            {
                // Best effort.
            }
        }
    }

    private async Task HandleAsync(JsonObject request)
    {
        var id = request["id"]!.GetValue<long>();
        var test = request["test"]?.GetValue<string>();
        var op = request["op"]!.GetValue<string>();
        var args = request["args"] as JsonObject ?? new JsonObject();
        var timeoutMs = request["timeoutMs"]?.GetValue<int>() ?? 0;
        Ops.Enqueue(op);

        JsonObject response;
        try
        {
            // Started synchronously, in arrival order (control protocol §6.1).
            var work = Dispatch(test, op, args);
            var value = timeoutMs > 0 ? await work.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs)) : await work;
            response = new JsonObject { ["t"] = "res", ["id"] = id, ["ok"] = true, ["value"] = value, ["hostNs"] = HostNs };
        }
        catch (Exception e)
        {
            response = new JsonObject { ["t"] = "res", ["id"] = id, ["ok"] = false, ["error"] = Error(e), ["hostNs"] = HostNs };
        }

        _outbound.Writer.TryWrite(Encoding.UTF8.GetBytes(response.ToJsonString()));
        if (op == "host.shutdown")
        {
            _outbound.Writer.TryWrite(Encoding.UTF8.GetBytes(new JsonObject { ["t"] = "bye", ["reason"] = "shutdown" }.ToJsonString()));
            await Task.Delay(100);
            Crash(0);
        }
    }

    private static JsonObject Error(Exception e)
    {
        if (e is AggregateException { InnerExceptions.Count: 1 } aggregate)
            e = aggregate.InnerExceptions[0];
        return e switch
        {
            UnknownHandleException => ControlErrors.Create(ControlErrors.UnknownHandle, e.Message),
            KeyNotFoundException or FormatException or InvalidCastException => ControlErrors.Create(ControlErrors.BadRequest, e.Message),
            _ => ControlErrors.FromException(e),
        };
    }

    private Task<JsonObject> Dispatch(string? test, string op, JsonObject args)
    {
        switch (op)
        {
            case "ping":
                return AnswerPings ? Task.FromResult(new JsonObject()) : new TaskCompletionSource<JsonObject>().Task;
            case "host.dumpThreads":
                return Task.FromResult(new JsonObject { ["text"] = "fake thread dump: " + _scopes.Count + " scope(s)" });
            case "host.shutdown":
            case "log.level":
                return Task.FromResult(new JsonObject());
            case "fake.hang":
                return new TaskCompletionSource<JsonObject>().Task;
            case "fake.sleep":
                return Sleep(args["ms"]!.GetValue<int>());
            case "test.begin":
                _scopes.TryAdd(Require(test), new FakeScope(this, test!));
                return Task.FromResult(new JsonObject());
            case "test.audit":
                return Scope(test).AuditAsync(args["h"]?.GetValue<string>(), args["pool"]?.GetValue<string>());
            case "test.end":
                if (!_scopes.TryRemove(Require(test), out var ended))
                    throw new ArgumentException("The test scope '" + test + "' is not open.");
                return ended.EndAsync();
            default:
                return Scope(test).DispatchAsync(op, args);
        }
    }

    private static async Task<JsonObject> Sleep(int ms)
    {
        await Task.Delay(ms);
        return new JsonObject();
    }

    private FakeScope Scope(string? test)
        => _scopes.TryGetValue(Require(test), out var scope) ? scope : throw new ArgumentException("The test scope '" + test + "' is not open.");

    private static string Require(string? test) => test ?? throw new ArgumentException("The op needs a test scope.");
}
