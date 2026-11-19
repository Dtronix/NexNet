using System.Buffers;
using System.Collections.Concurrent;
using NexNet.IntegrationTests.Pipes;
using NexNet.IntegrationTests.TestInterfaces;
using NexNet.Logging;
using NexNet.Transports;
using NexNet.Transports.HttpSocket;
using NexNet.Transports.WebSocket;

namespace NexNet.IntegrationTests;

/// <summary>
/// Regression stress for transports that returned a receive buffer to the shared pool while a read was still writing
/// into it (S28): the late bytes landed in another session's data. Many clients connect, echo a unique payload and
/// disconnect concurrently while the peer floods them (half closed by the client, half by the server). Every received
/// buffer and payload is checked for bytes that belong to another session, and no session may end with a protocol
/// error.
/// </summary>
/// <remarks>
/// The iteration count per transport is bounded for the regular suite; set NEXNET_STRESS_ITERATIONS for a long local
/// run (the fix was verified with 20000 per transport).
/// </remarks>
[NotInParallel("stress")]
internal class TransportBufferIsolationStressTests : BaseTests
{
    private const int DefaultIterations = 1000;
    private const int Workers = 8;
    private const int FloodConcurrency = 4;
    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(15);

    // NnP + DC4: the protocol header each side sends once, at the start of its stream.
    private static readonly byte[] ProtocolMagic = [(byte)'N', (byte)'n', (byte)'P', 0x14];

    // Sent back and forth while sessions close, so reads are in flight and receiving data when a transport closes.
    private static readonly byte[] FloodPayload = CreateFloodPayload();

    private static int Iterations =>
        int.TryParse(Environment.GetEnvironmentVariable("NEXNET_STRESS_ITERATIONS"), out var n) && n > 0
            ? n
            : DefaultIterations;

    [Test]
    [Arguments(Type.HttpSocket)]
    [Arguments(Type.WebSocket)]
    public async Task ClosingSessionsNeverLeakReceiveBuffers(Type type)
    {
        var failures = new ConcurrentQueue<string>();
        var url = await StartServer(type, failures);

        var errors = await RunWorkers(Iterations, _ => (type, url), failures);

        await AssertNoCorruption(Iterations, failures, errors);
    }

    [Test]
    public async Task ClosingSessionsNeverLeakReceiveBuffers_MixedTransports()
    {
        var failures = new ConcurrentQueue<string>();
        var httpSocketUrl = await StartServer(Type.HttpSocket, failures);
        var webSocketUrl = await StartServer(Type.WebSocket, failures);

        var errors = await RunWorkers(Iterations * 2,
            worker => worker % 2 == 0 ? (Type.HttpSocket, httpSocketUrl) : (Type.WebSocket, webSocketUrl),
            failures);

        await AssertNoCorruption(Iterations * 2, failures, errors);
    }

    /// <summary>
    /// No foreign bytes anywhere. An iteration that failed with an error (a connect refused by an exhausted port
    /// range on a loaded machine, a timeout) is tolerated up to 1% of the iterations: corruption shows as a protocol
    /// error or a payload check, which are failures.
    /// </summary>
    private static async Task AssertNoCorruption(int iterations, ConcurrentQueue<string> failures,
        ConcurrentQueue<string> errors)
    {
        var output = TestContext.Current?.Output.StandardOutput;
        output?.WriteLine($"{iterations} iterations: {failures.Count} failure(s), {errors.Count} tolerated error(s).");
        foreach (var error in errors.Take(20))
            output?.WriteLine(error);
        await Assert.That(failures.Count).IsEqualTo(0).Because(Describe("failure", failures));
        await Assert.That(errors.Count).IsLessThanOrEqualTo(Math.Max(2, iterations / 100))
            .Because(Describe("error", errors));
    }

    private async Task<Uri> StartServer(Type type, ConcurrentQueue<string> failures)
    {
        // A new port for each server.
        CurrentTcpPort = null;
        var config = CreateServerConfig(type, BasePipeTests.LogMode.None);
        var port = CurrentTcpPort!.Value;

        var streams = new ConcurrentDictionary<long, StreamCheck>();
        config.InternalOnReceive = (session, buffer) =>
        {
            var error = streams.GetOrAdd(session.Id, static _ => new StreamCheck()).Check(buffer);
            if (error != null)
                failures.Enqueue($"{type} server session {session.Id}: {error}");
            return ValueTask.CompletedTask;
        };

        config.Logger = new FailureLogger($"{type} server", failures);
        // Close at once, so a closing transport still has reads in flight that receive the peer's flood.
        config.DisconnectDelay = 0;

        var server = CreateServer(config, nexus =>
        {
            // Close the session from the server while the client floods it.
            nexus.ServerVoidEvent = static n => _ = Task.Run(async () =>
            {
                await Task.Delay(2).ConfigureAwait(false);
                await n.Context.DisconnectAsync().ConfigureAwait(false);
            });

            // Flood the client until its session closes.
            nexus.ServerTaskEvent = static n =>
            {
                var caller = n.Context.Clients.Caller;
                return new ValueTask(Flood(() => caller.ClientData(FloodPayload)));
            };

            nexus.ServerDataEvent = (_, data) =>
            {
                if (!data.AsSpan().SequenceEqual(FloodPayload))
                    failures.Enqueue($"{type} server: flood payload differs from the one sent.");
                return ValueTask.CompletedTask;
            };
            nexus.OnDisconnectedEvent = static _ => ValueTask.CompletedTask;
        });
        await server.StartAsync().Timeout(T(TestTimeouts.Connect));

        return type == Type.HttpSocket
            ? new Uri($"http://127.0.0.1:{port}/httpsocket-test")
            : new Uri($"ws://127.0.0.1:{port}/websocket-test");
    }

    private static async Task<ConcurrentQueue<string>> RunWorkers(int iterations,
        Func<int, (Type Type, Uri Url)> target, ConcurrentQueue<string> failures)
    {
        var errors = new ConcurrentQueue<string>();
        var maxErrors = Math.Max(2, iterations / 100);
        var next = -1;
        var workers = new Task[Workers];
        for (var worker = 0; worker < Workers; worker++)
        {
            var (type, url) = target(worker);
            workers[worker] = Task.Run(async () =>
            {
                int iteration;
                while ((iteration = Interlocked.Increment(ref next)) < iterations
                       && failures.IsEmpty
                       && errors.Count <= maxErrors)
                {
                    try
                    {
                        await RunIteration(type, url, iteration, failures).ConfigureAwait(false);
                    }
                    catch (Exception e)
                    {
                        errors.Enqueue($"{type} iteration {iteration}: {e.Message}");
                    }
                }
            });
        }

        await Task.WhenAll(workers).ConfigureAwait(false);
        return errors;
    }

    private static async Task RunIteration(Type type, Uri url, int iteration, ConcurrentQueue<string> failures)
    {
        ClientConfig config = type == Type.HttpSocket
            ? new HttpSocketClientConfig { Url = url }
            : new WebSocketClientConfig { Url = url };

        var stream = new StreamCheck();
        config.InternalOnReceive = (_, buffer) =>
        {
            var error = stream.Check(buffer);
            if (error != null)
                failures.Enqueue($"{type} client iteration {iteration}: {error}");
            return ValueTask.CompletedTask;
        };

        config.Logger = new FailureLogger($"{type} client iteration {iteration}", failures);
        config.DisconnectDelay = 0;

        var flooded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clientNexus = new ClientNexus
        {
            ClientDataEvent = (_, data) =>
            {
                if (!data.AsSpan().SequenceEqual(FloodPayload))
                    failures.Enqueue($"{type} client iteration {iteration}: flood payload differs from the one sent.");
                flooded.TrySetResult();
                return ValueTask.CompletedTask;
            }
        };
        var client = ClientNexus.CreateClient(config, clientNexus);
        var step = "create";
        try
        {
            step = "connect";
            await client.ConnectAsync().Timeout(T(TestTimeouts.Connect)).ConfigureAwait(false);

            var payload = new byte[256 + iteration % 4096];
            for (var i = 0; i < payload.Length; i++)
                payload[i] = unchecked((byte)(iteration * 7 + i * 31));

            step = "echo";
            var echo = await client.Proxy.ServerEcho(payload).AsTask().WaitAsync(StepTimeout).ConfigureAwait(false);
            if (!echo.AsSpan().SequenceEqual(payload))
                failures.Enqueue($"{type} client iteration {iteration}: echo differs from the payload sent.");

            // Half the sessions are closed by the client while the server floods it, half by the server while the
            // client floods it.
            if (iteration % 2 == 0)
            {
                step = "client close";
                var flood = client.Proxy.ServerTask().AsTask();
                await flooded.Task.WaitAsync(StepTimeout).ConfigureAwait(false);
                await client.DisconnectAsync().WaitAsync(StepTimeout).ConfigureAwait(false);
                await WaitFlood(flood, $"{type} client iteration {iteration} (client close)", failures).ConfigureAwait(false);
            }
            else
            {
                step = "server close";
                // Asked first: the server closes shortly after, while the flood runs.
                var proxy = client.Proxy;
                proxy.ServerVoid();
                var flood = Flood(() => proxy.ServerData(FloodPayload));
                await client.DisconnectedTask.WaitAsync(StepTimeout).ConfigureAwait(false);
                await WaitFlood(flood, $"{type} client iteration {iteration} (server close)", failures).ConfigureAwait(false);
            }
        }
        catch (Exception e)
        {
            throw new InvalidOperationException($"{step} failed ({client.State}): {e.GetType().Name}: {e.Message}", e);
        }
        finally
        {
            await client.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Checks the reads of one session: the peer's protocol header may only appear at the start of its stream, so a
    /// header anywhere else is another session's data.
    /// </summary>
    private sealed class StreamCheck
    {
        private bool _headerRead;

        public string? Check(ReadOnlySequence<byte> buffer)
        {
            // A read returns the unconsumed bytes, so the header is at offset 0 until 8 bytes have been read.
            var bytes = buffer.ToArray();
            var index = bytes.AsSpan().IndexOf(ProtocolMagic);
            var error = index > 0 || (index == 0 && _headerRead)
                ? $"protocol header at offset {index} of a {bytes.Length} byte read."
                : null;
            if (bytes.Length >= 8)
                _headerRead = true;
            return error;
        }
    }

    /// <summary>
    /// Sends with several calls in flight until the session closes (a call fails) or 10 seconds pass.
    /// </summary>
    private static Task Flood(Func<ValueTask> send)
    {
        var deadline = Environment.TickCount64 + 10_000;
        var senders = new Task[FloodConcurrency];
        for (var i = 0; i < senders.Length; i++)
        {
            senders[i] = Task.Run(async () =>
            {
                try
                {
                    while (Environment.TickCount64 < deadline)
                        await send().ConfigureAwait(false);
                }
                catch
                {
                    // The session closed.
                }
            });
        }

        return Task.WhenAll(senders);
    }

    /// <summary>
    /// Waits for every call of the flood: they all end with the session. A call still pending after the session has
    /// disconnected never completes, which is a failure (S30: a call registered while the session was closing stayed
    /// pending forever, about 1 in 3000 server closes).
    /// </summary>
    private static async Task WaitFlood(Task flood, string name, ConcurrentQueue<string> failures)
    {
        if (await Task.WhenAny(flood, Task.Delay(StepTimeout)).ConfigureAwait(false) != flood)
        {
            failures.Enqueue($"{name}: a call was still pending {StepTimeout.TotalSeconds:0} s after the disconnect.");
            _ = flood.ContinueWith(static t => _ = t.Exception, TaskScheduler.Default);
        }
    }

    private static byte[] CreateFloodPayload()
    {
        var payload = new byte[8 * 1024];
        for (var i = 0; i < payload.Length; i++)
            payload[i] = unchecked((byte)(i * 13 + 5));
        return payload;
    }

    /// <summary>
    /// Records sessions that ended with a protocol error, which foreign bytes in a stream cause. Socket errors are
    /// expected: sessions close abruptly.
    /// </summary>
    private sealed class FailureLogger(string name, ConcurrentQueue<string> failures) : INexusLogger
    {
        public NexusLogBehaviors Behaviors { get; set; }
        public string FormattedPath => name;
        public string? PathSegment { get; set; }

        public void Log(NexusLogLevel logLevel, string? category, Exception? exception, string message)
        {
            if (message.StartsWith("Session disconnected with reason: ProtocolError", StringComparison.Ordinal))
                failures.Enqueue($"{name}: {logLevel} {message} {exception?.GetType().Name} {exception?.Message}");
        }

        public INexusLogger CreateLogger(string? pathSegment = null) => this;
    }

    private static string Describe(string kind, ConcurrentQueue<string> items)
        => $"{items.Count} {kind}(s):{Environment.NewLine}{string.Join(Environment.NewLine, items.Take(20))}";
}
