using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace NexNet.Interop.Remote;

/// <summary>An event of a language host (control protocol §4.4, §7).</summary>
/// <param name="Test">The test scope, or null.</param>
/// <param name="Handle">The client handle, or null.</param>
/// <param name="Name">The event name.</param>
/// <param name="Data">The event data (never null).</param>
/// <param name="HostNs">The host's monotonic time in nanoseconds.</param>
public sealed record ControlEvent(string? Test, string? Handle, string Name, JsonObject Data, long HostNs);

/// <summary>
/// The runner's end of a control connection: correlates requests with responses, routes events, keeps the transcript
/// (docs/internals/interop-control-protocol.md). Requests are written in the order <see cref="RequestAsync"/> is called.
/// </summary>
public sealed class ControlConnection : IAsyncDisposable
{
    private sealed record Pending(string? Test, string Op, TaskCompletionSource<JsonObject> Completion);

    private readonly Stream _stream;
    private readonly ConcurrentDictionary<long, Pending> _pending = new();
    private readonly ConcurrentDictionary<(string?, string?), ImmutableHandlers> _handlers = new();
    private readonly ConcurrentDictionary<string, Task> _scopes = new(StringComparer.Ordinal);
    private readonly Channel<byte[]> _outbound = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _closing = new();
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // True while this thread dispatches a frame of some connection's read loop; a response cannot arrive until it ends.
    [ThreadStatic]
    private static bool t_dispatching;
    private Exception? _failure;
    private long _nextId;
    private int _started;

    /// <summary>Creates a connection over a stream that has already passed the hello.</summary>
    /// <param name="stream">The connected stream; owned by the connection.</param>
    /// <param name="hello">The host's hello.</param>
    /// <param name="transcript">The transcript to record into; null creates one.</param>
    public ControlConnection(Stream stream, HostHello hello, ControlTranscript? transcript = null)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        Hello = hello ?? throw new ArgumentNullException(nameof(hello));
        Transcript = transcript ?? new ControlTranscript();
    }

    /// <summary>The host's hello.</summary>
    public HostHello Hello { get; }

    /// <summary>The transcript of this connection.</summary>
    public ControlTranscript Transcript { get; }

    /// <summary>Added to a request's <c>timeoutMs</c> before the runner gives up on its response.</summary>
    public TimeSpan ResponseGrace { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Completes when the connection ends; faulted with the reason unless it was disposed.</summary>
    public Task Completion => _completion.Task;

    /// <summary>True once the connection failed or closed; requests then throw <see cref="Failure"/>.</summary>
    public bool IsClosed => Volatile.Read(ref _failure) != null;

    /// <summary>Why the connection ended, or null while it is open.</summary>
    public Exception? Failure => Volatile.Read(ref _failure);

    /// <summary>Raised (on the read loop) for events no subscription took: host-level events, unknown handles.</summary>
    public event Action<ControlEvent>? UnroutedEvent;

    /// <summary>Starts the read and write loops.</summary>
    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
            return;
        _ = Task.Run(WriteLoopAsync);
        _ = Task.Run(ReadLoopAsync);
    }

    /// <summary>
    /// Sends a request and returns its result object. The request is queued for writing before this method returns, so
    /// requests are written in call order.
    /// </summary>
    /// <param name="test">The test scope, or null for host-level ops.</param>
    /// <param name="op">The operation.</param>
    /// <param name="args">The arguments, or null.</param>
    /// <param name="timeoutMs">The op's bound sent to the host (0: none). The runner waits that long plus <see cref="ResponseGrace"/>.</param>
    /// <param name="deadline">Overrides how long the runner waits for the response.</param>
    /// <exception cref="TimeoutException">No response in time.</exception>
    public Task<JsonObject> RequestAsync(string? test, string op, JsonObject? args = null, int timeoutMs = 0, TimeSpan? deadline = null)
    {
        ArgumentNullException.ThrowIfNull(op);
        if (Volatile.Read(ref _failure) is { } failure)
            return Task.FromException<JsonObject>(failure);

        var id = Interlocked.Increment(ref _nextId);
        var message = new JsonObject { ["t"] = "req", ["id"] = id };
        if (test != null)
            message["test"] = test;
        message["op"] = op;
        if (args != null)
            message["args"] = args;
        if (timeoutMs > 0)
            message["timeoutMs"] = timeoutMs;

        var pending = new Pending(test, op, new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously));
        _pending[id] = pending;
        var text = message.ToJsonString();
        Transcript.Record(test, ">>", text);
        if (!_outbound.Writer.TryWrite(System.Text.Encoding.UTF8.GetBytes(text)))
        {
            _pending.TryRemove(id, out _);
            return Task.FromException<JsonObject>(Failure ?? new ControlProtocolException("The control connection is closed."));
        }

        // Fail() may have run between the check above and the registration.
        if (Volatile.Read(ref _failure) is { } late && _pending.TryRemove(id, out _))
            return Task.FromException<JsonObject>(late);

        var wait = deadline ?? (timeoutMs > 0 ? TimeSpan.FromMilliseconds(timeoutMs) + ResponseGrace : (TimeSpan?)null);
        return wait == null ? pending.Completion.Task : WithDeadlineAsync(id, pending, wait.Value);
    }

    /// <summary>
    /// Opens the test scope <paramref name="test"/> once per connection (<c>test.begin</c>); later calls return the same
    /// task. The request is queued before this method returns.
    /// </summary>
    public Task BeginTestAsync(string test)
    {
        ArgumentNullException.ThrowIfNull(test);
        return _scopes.GetOrAdd(test, t => RequestAsync(t, "test.begin", timeoutMs: 30_000));
    }

    /// <summary>Closes the test scope (<c>test.end</c>) and returns its leak report; null when the scope was never opened.</summary>
    public async Task<LeakReport?> EndTestAsync(string test)
    {
        if (!_scopes.TryRemove(test, out var begun))
            return null;
        await begun.ConfigureAwait(false);
        var report = await RequestAsync(test, "test.end", timeoutMs: 30_000).ConfigureAwait(false);
        return LeakReports.Parse(report);
    }

    /// <summary>
    /// True on a read loop while it dispatches a frame (event handlers and what they run inline). Waiting for a response
    /// there blocks the loop that would deliver it.
    /// </summary>
    internal static bool IsDispatching => t_dispatching;

    /// <summary>True when <paramref name="test"/> has an open scope on this connection.</summary>
    public bool HasScope(string test) => _scopes.ContainsKey(test);

    /// <summary>
    /// Whether a request of a test scope is waiting for its response: the host is working for a test, and may answer a
    /// ping late while it does (<see cref="Hosts.LanguageHostOptions.BusyPingTimeout"/>).
    /// </summary>
    public bool HasTestRequestsInFlight => _pending.Values.Any(p => p.Test != null);

    /// <summary>Routes events of (<paramref name="test"/>, <paramref name="handle"/>) to <paramref name="handler"/> (called on the read loop; must not block).</summary>
    public IDisposable Subscribe(string? test, string? handle, Action<ControlEvent> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        var key = (test, handle);
        _handlers.AddOrUpdate(key, _ => new ImmutableHandlers([handler]), (_, h) => h.Add(handler));
        return new Subscription(this, key, handler);
    }

    /// <summary>
    /// Ends the connection: every request in flight and every later request fails with <paramref name="reason"/>. The
    /// first reason wins.
    /// </summary>
    public void Fail(Exception reason)
    {
        ArgumentNullException.ThrowIfNull(reason);
        if (Interlocked.CompareExchange(ref _failure, reason, null) != null)
            return;

        Transcript.Record(null, "!!", "connection failed: " + reason.Message);
        _outbound.Writer.TryComplete();
        _closing.Cancel();
        foreach (var id in _pending.Keys)
        {
            if (_pending.TryRemove(id, out var pending))
                pending.Completion.TrySetException(reason);
        }

        try
        {
            _stream.Dispose();
        }
        catch (Exception)
        {
            // Already closed.
        }

        if (reason is ConnectionClosedException)
            _completion.TrySetResult();
        else
            _completion.TrySetException(reason);
    }

    /// <summary>Sends <c>bye</c> (best effort) and closes the connection.</summary>
    public async ValueTask DisposeAsync()
    {
        if (!IsClosed)
        {
            try
            {
                var bye = System.Text.Encoding.UTF8.GetBytes(new JsonObject { ["t"] = "bye", ["reason"] = "runner closing" }.ToJsonString());
                _outbound.Writer.TryWrite(bye);
                _outbound.Writer.TryComplete();
                await _writerDone.Task.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Best effort.
            }
        }

        Fail(new ConnectionClosedException());
        _closing.Dispose();
    }

    private readonly TaskCompletionSource _writerDone = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private async Task<JsonObject> WithDeadlineAsync(long id, Pending pending, TimeSpan wait)
    {
        try
        {
            return await pending.Completion.Task.WaitAsync(wait).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _pending.TryRemove(id, out _);
            Transcript.Record(pending.Test, "!!", $"no response to request {id} ({pending.Op}) within {wait.TotalMilliseconds:F0} ms");
            throw new TimeoutException($"The {Hello.Lang} host did not answer '{pending.Op}' (request {id}) within {wait.TotalMilliseconds:F0} ms.");
        }
    }

    private async Task WriteLoopAsync()
    {
        try
        {
            await foreach (var payload in _outbound.Reader.ReadAllAsync().ConfigureAwait(false))
                await ControlFraming.WriteAsync(_stream, ControlFrameKind.Json, payload, _closing.Token).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            Fail(new HostCrashedException($"Writing to the {Hello.Lang} host failed: {e.Message}", innerException: e));
        }
        finally
        {
            _writerDone.TrySetResult();
        }
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (true)
            {
                var frame = await ControlFraming.ReadAsync(_stream, _closing.Token).ConfigureAwait(false);
                if (frame == null)
                {
                    Fail(new HostCrashedException($"The {Hello.Lang} host closed the control connection."));
                    return;
                }

                JsonObject message;
                try
                {
                    message = JsonNode.Parse(frame.Value.Payload) as JsonObject
                              ?? throw new ControlProtocolException("A control frame does not hold a JSON object.");
                }
                catch (JsonException e)
                {
                    throw new ControlProtocolException("A control frame holds invalid JSON: " + e.Message, e);
                }

                t_dispatching = true;
                try
                {
                    Dispatch(message, frame.Value.Payload.Length);
                }
                finally
                {
                    t_dispatching = false;
                }
            }
        }
        catch (Exception e) when (IsClosed)
        {
            _ = e; // Closed on purpose or already failed.
        }
        catch (ControlProtocolException e)
        {
            Fail(e);
        }
        catch (Exception e)
        {
            Fail(new HostCrashedException($"Reading from the {Hello.Lang} host failed: {e.Message}", innerException: e));
        }
    }

    private void Dispatch(JsonObject message, int length)
    {
        var type = message["t"]?.GetValue<string>();
        switch (type)
        {
            case "res":
            {
                var id = message["id"]?.GetValue<long>() ?? throw new ControlProtocolException("A response has no id.");
                _pending.TryRemove(id, out var pending);
                Transcript.Record(pending?.Test, "<<", Text(message, length));
                if (pending == null)
                    return; // Gave up on it (deadline) or never sent.

                if (message["ok"]?.GetValue<bool>() == true)
                    pending.Completion.TrySetResult(message["value"] as JsonObject ?? new JsonObject());
                else
                    pending.Completion.TrySetException(ControlErrors.ToException(message["error"]));
                return;
            }
            case "evt":
            {
                var test = message["test"]?.GetValue<string>();
                Transcript.Record(test, "<<", Text(message, length));
                var evt = new ControlEvent(
                    test,
                    message["h"]?.GetValue<string>(),
                    message["ev"]?.GetValue<string>() ?? throw new ControlProtocolException("An event has no name."),
                    (message["data"] as JsonObject)?.DeepClone() as JsonObject ?? new JsonObject(),
                    message["hostNs"]?.GetValue<long>() ?? 0);
                Route(evt);
                return;
            }
            case "bye":
                Transcript.Record(null, "<<", Text(message, length));
                Fail(new HostCrashedException($"The {Hello.Lang} host closed the control connection: {message["reason"]?.GetValue<string>()}"));
                return;
            default:
                throw new ControlProtocolException($"Unknown control message type '{type}'.");
        }
    }

    private void Route(ControlEvent evt)
    {
        var routed = false;
        foreach (var key in new[] { (evt.Test, evt.Handle), (evt.Test, (string?)null) })
        {
            if (!_handlers.TryGetValue(key, out var handlers))
                continue;
            foreach (var handler in handlers.Items)
            {
                routed = true;
                try
                {
                    handler(evt);
                }
                catch (Exception e)
                {
                    Transcript.Record(evt.Test, "!!", $"event handler of {evt.Name} failed: {e.Message}");
                }
            }

            if (evt.Handle == null)
                break;
        }

        if (!routed)
            UnroutedEvent?.Invoke(evt);
    }

    private static string Text(JsonObject message, int length)
        => length > 64 * 1024 ? "(" + length + " bytes) " + message.ToJsonString()[..4096] : message.ToJsonString();

    private sealed class ImmutableHandlers(Action<ControlEvent>[] items)
    {
        public Action<ControlEvent>[] Items { get; } = items;

        public ImmutableHandlers Add(Action<ControlEvent> handler) => new([.. Items, handler]);

        public ImmutableHandlers Remove(Action<ControlEvent> handler) => new(Items.Where(h => h != handler).ToArray());
    }

    private sealed class Subscription(ControlConnection owner, (string?, string?) key, Action<ControlEvent> handler) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                owner._handlers.AddOrUpdate(key, _ => new ImmutableHandlers([]), (_, h) => h.Remove(handler));
        }
    }

    /// <summary>The reason of a connection the runner closed on purpose.</summary>
    private sealed class ConnectionClosedException() : ObjectDisposedException("ControlConnection", "The control connection was closed.");
}
