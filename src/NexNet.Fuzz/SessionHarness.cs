using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipelines;
using System.Threading.Channels;
using NexNet.Invocation;
using NexNet.Logging;
using NexNet.Messages;
using NexNet.Pipes;
using NexNet.Serialization;
using NexNet.Transports;

namespace NexNet.Fuzz;

/// <summary>
/// Harness 3: the server session's receive loop. Each input is one connection to a long-lived server over an
/// in-memory transport. The stream ends after the input, and the session must then shut down promptly.
/// </summary>
/// <remarks>
/// Input layout: byte 0 holds flags, the rest is the stream.
/// <list type="bullet">
/// <item>Bit 0 clear: a valid protocol preamble is sent first. Set: the input supplies its own preamble.</item>
/// <item>Bit 1 clear: a valid <c>ClientGreeting</c> follows the preamble, so the fuzzed bytes reach invocations, pipe
/// writes and the rest of the post-handshake protocol. Set: the input supplies its own greeting.</item>
/// </list>
/// Failures: an exception escaping the harness, a session still open <see cref="SessionTimeout"/> after the stream
/// ends, or the server logging an exception type that hostile input should never cause.
/// </remarks>
public static class SessionHarness
{
    public static TimeSpan SessionTimeout = TimeSpan.FromSeconds(double.TryParse(Environment.GetEnvironmentVariable("NEXNET_FUZZ_SESSION_TIMEOUT"), out var s) ? s : 5);

    private static readonly Lazy<Server> Instance = new(() => Server.Start());

    public static void Run(byte[] data)
    {
        var flags = data.Length > 0 ? data[0] : (byte)0;
        var payload = data.Length > 0 ? data.AsSpan(1) : ReadOnlySpan<byte>.Empty;

        var server = Instance.Value;
        var transport = new FuzzTransport();
        server.Accept(transport);

        var input = transport.ClientToServer.Writer;
        if ((flags & 1) == 0)
            Write(input, Server.Preamble);
        if ((flags & 1) == 0 && (flags & 2) == 0)
            Write(input, server.Greeting);
        Write(input, payload);
        input.Complete();

        if (!transport.Closed.Wait(SessionTimeout))
            throw new TimeoutException($"Session did not close within {SessionTimeout.TotalSeconds:F0} s after the stream ended.");

        server.Logger.ThrowIfUnexpected();
    }

    /// <summary>
    /// Seed inputs: valid post-handshake traffic for every server method, plus pipe and channel writes, a
    /// cancellation and a few malformed preambles. Method IDs follow declaration order in <see cref="IFuzzServerNexus"/>.
    /// </summary>
    public static IEnumerable<byte[]> Seeds()
    {
        var order = new FuzzOrder
        {
            Id = 7,
            Customer = "c",
            Lines = [new FuzzLine { Quantity = 1, Price = 2.5m }],
            Totals = new Dictionary<string, double> { ["t"] = 1.5 },
            Shape = new FuzzCircle { Radius = 2 },
            Codes = [1, 2, 3],
        };

        byte[] Args(params Action<MsgPackWriterBox>[] writes)
        {
            var box = new MsgPackWriterBox();
            box.Begin(writes.Length);
            foreach (var write in writes)
                write(box);
            return box.End();
        }

        byte[] Invoke(ushort id, ushort method, byte[] arguments, InvocationFlags flags = InvocationFlags.None)
            => Frame(MessageType.Invocation, new InvocationMessage { InvocationId = id, MethodId = method, Flags = flags, Arguments = arguments });

        byte[] PipeWrite(byte clientId, byte serverId, byte[] data)
        {
            var frame = new byte[5 + data.Length];
            frame[0] = (byte)MessageType.DuplexPipeWrite;
            BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(1), (ushort)data.Length);
            frame[3] = clientId;
            frame[4] = serverId;
            data.CopyTo(frame, 5);
            return frame;
        }

        byte[] PipeState(byte clientId, byte serverId, NexusDuplexPipe.State state)
            => Frame(MessageType.DuplexPipeUpdateState, new DuplexPipeUpdateStateMessage { PipeId = (ushort)(clientId | (serverId << 8)), State = state });

        static byte[] Cat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

        var orderBytes = NexusSerializer.Serialize(order);
        var ints = Args(); // reused below for an empty argument array
        var calls = new List<byte[]>
        {
            Invoke(1, 0, ints),
            Invoke(2, 1, Args(b => b.Write(1), b => b.Write(2))),
            Invoke(3, 2, Args(b => b.Value(order))),
            Invoke(4, 3, Args(b => b.Value(order))),
            Invoke(5, 4, Args(b => b.Write("a"), b => b.Value(new[] { "b" }), b => b.Value(new Dictionary<string, int> { ["c"] = 1 }))),
            Invoke(6, 5, Args(b => b.Value<IFuzzShape>(new FuzzSquare { Nested = order }))),
            Invoke(7, 6, Args(b => b.Value(new long[] { 1, 2 }), b => b.Value(new[] { 1.5 }), b => b.Value(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)), b => b.Value(Guid.Empty), b => b.Value(1.5m))),
            Cat(Invoke(8, 7, Args(b => b.Write(40))), Frame(MessageType.InvocationCancellation, new InvocationCancellationMessage { InvocationId = 8 })),
            Cat(Invoke(9, 8, Args(b => b.Write(1))), PipeWrite(1, 1, Cat(orderBytes, orderBytes, orderBytes[..5])), PipeState(1, 1, NexusDuplexPipe.State.ClientWriterServerReaderComplete)),
            Cat(Invoke(10, 9, Args(b => b.Write(2))), PipeWrite(2, 1, [1, 2, 0xcd, 0x01, 0x00, 0xd2, 0, 0, 1, 0]), PipeState(2, 1, NexusDuplexPipe.State.ClientWriterServerReaderComplete)),
            Cat(Invoke(11, 10, Args(b => b.Write(3))), PipeWrite(3, 1, [1, 2, 3, 4, 5, 6, 7, 8]), PipeState(3, 1, NexusDuplexPipe.State.ClientWriterServerReaderComplete)),
        };

        foreach (var call in calls)
            yield return [0, .. call];

        yield return [0, .. calls.SelectMany(c => c)];

        // Own preamble (flag bit 0): truncated, wrong magic, wrong version, valid without a greeting.
        yield return [1, 0x4E, 0x6E, 0x50];
        yield return [1, 0x4E, 0x6E, 0x50, 0x15, 2, 0, 0, 2];
        yield return [1, .. Server.Preamble.AsSpan(0, 7), 9];
        yield return [1, .. Server.Preamble];

        // Own greeting (flag bit 1): the greeting itself is fuzzed.
        yield return [2, .. Instance.Value.Greeting];
    }

    /// <summary>
    /// Builds a MessagePack argument array. Exists so seed lambdas can share one writer (ref structs can't be captured).
    /// </summary>
    private sealed class MsgPackWriterBox
    {
        private readonly ArrayBufferWriter<byte> _buffer = new();
        private int _count;

        public void Begin(int count) => _count = count;
        public void Write(int value) => Append((ref MsgPackWriter w) => w.Write(value));
        public void Write(string value) => Append((ref MsgPackWriter w) => w.Write(value));
        public void Value<T>(T value) => _buffer.Write(NexusSerializer.Serialize(value));

        private void Append(WriterAction action)
        {
            var writer = new MsgPackWriter(_buffer);
            action(ref writer);
            writer.Flush();
        }

        public byte[] End()
        {
            var header = new ArrayBufferWriter<byte>();
            var writer = new MsgPackWriter(header);
            writer.WriteArrayHeader(_count);
            writer.Flush();
            return [.. header.WrittenSpan, .. _buffer.WrittenSpan];
        }

        private delegate void WriterAction(ref MsgPackWriter writer);
    }

    private static byte[] Frame<T>(MessageType type, T message)
        where T : IMessageBase
        => Server.FrameMessage(type, message);

    private static void Write(PipeWriter writer, ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 0)
            return;
        writer.Write(bytes);
        writer.FlushAsync().AsTask().GetAwaiter().GetResult();
    }

    private sealed class Server
    {
        public static readonly byte[] Preamble = [0x4E, 0x6E, 0x50, 0x14, (byte)PayloadFormatValue, 0, 0, 2];

        // Payload format byte of this build (2 = MessagePack, 1 = MemoryPack).
#if NEXNET_MEMORYPACK
        private const int PayloadFormatValue = 1;
#else
        private const int PayloadFormatValue = 2;
#endif

        private readonly FuzzListener _listener = new();
        public FuzzLogger Logger { get; } = new();
        public byte[] Greeting { get; private set; } = [];

        public static Server Start()
        {
            var server = new Server();
            // No linger after sending a disconnect message: it only adds wall time per input.
            // Short idle timeout: when a connection's invocation slots are full (MaxConcurrentConnectionInvocations),
            // the receive loop stops reading by design, so it can't see the stream end and only the idle timeout
            // closes it. 2 s (checked every Timeout / 4) keeps that inside SessionTimeout; a session still open
            // after it is a real hang.
            var config = new FuzzServerConfig(server._listener) { Logger = server.Logger, DisconnectDelay = 0, Timeout = 2000 };
            var nexusServer = FuzzServerNexus.CreateServer(config, static () => new FuzzServerNexus());
            nexusServer.StartAsync().GetAwaiter().GetResult();

            var greeting = new ClientGreetingMessage
            {
                Version = null,
                ServerNexusHash = IInvocationMethodHash.GetMethodHash<FuzzServerNexus>(),
                ClientNexusHash = IInvocationMethodHash.GetMethodHash<FuzzClientNexus>(),
            };
            server.Greeting = FrameMessage(MessageType.ClientGreeting, greeting);
            return server;
        }

        public void Accept(FuzzTransport transport) => _listener.Pending.Writer.TryWrite(transport);

        internal static byte[] FrameMessage<T>(MessageType type, T message)
            where T : IMessageBase
        {
            var body = new ArrayBufferWriter<byte>();
            var writer = new MsgPackWriter(body);
            message.Serialize(ref writer);
            writer.Flush();

            var frame = new byte[3 + body.WrittenCount];
            frame[0] = (byte)type;
            BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(1), (ushort)body.WrittenCount);
            body.WrittenSpan.CopyTo(frame.AsSpan(3));
            return frame;
        }
    }

    private sealed class FuzzServerConfig : ServerConfig
    {
        private readonly FuzzListener _listener;

        public FuzzServerConfig(FuzzListener listener)
            : base(ServerConnectionMode.Listener)
        {
            _listener = listener;
        }

        protected override ValueTask<ITransportListener?> OnCreateServerListener(CancellationToken cancellationToken)
            => new(_listener);
    }

    private sealed class FuzzListener : ITransportListener
    {
        public readonly Channel<ITransport> Pending = Channel.CreateUnbounded<ITransport>();

        public ValueTask CloseAsync(bool linger)
        {
            Pending.Writer.TryComplete();
            return default;
        }

        public async ValueTask<ITransport?> AcceptTransportAsync(CancellationToken cancellationToken)
        {
            try
            {
                return await Pending.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (ChannelClosedException)
            {
                return null;
            }
        }
    }

    private sealed class FuzzTransport : ITransport
    {
        // No back pressure: the harness never reads what the server sends, so writes must not block.
        private static readonly PipeOptions Options = new(pauseWriterThreshold: 0, resumeWriterThreshold: 0, useSynchronizationContext: false);

        public readonly Pipe ClientToServer = new(Options);
        private readonly Pipe _serverToClient = new(Options);
        private readonly ManualResetEventSlim _closed = new();

        public ManualResetEventSlim Closed => _closed;
        public PipeReader Input => ClientToServer.Reader;
        public PipeWriter Output => _serverToClient.Writer;
        public string? RemoteAddress => "fuzz";
        public int? RemotePort => null;

        public ValueTask CloseAsync(bool linger)
        {
            _serverToClient.Writer.Complete();
            _serverToClient.Reader.Complete();
            _closed.Set();
            return default;
        }
    }

    /// <summary>
    /// Records exceptions the server logs at Error or above. Hostile input must only produce protocol and
    /// serialization errors, so anything else is reported as a failure.
    /// </summary>
    public sealed class FuzzLogger : INexusLogger
    {
        private Exception? _unexpected;

        public NexusLogBehaviors Behaviors { get; set; }
        public string FormattedPath => "fuzz";
        public string? PathSegment { get; set; }

        // NEXNET_FUZZ_VERBOSE=1 prints every server log line with a timestamp (for investigating a reproducer).
        private static readonly bool Verbose = Environment.GetEnvironmentVariable("NEXNET_FUZZ_VERBOSE") == "1";
        private static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();

        public void Log(NexusLogLevel logLevel, string? category, Exception? exception, string message)
        {
            if (Verbose)
                Console.WriteLine($"[{Clock.Elapsed.TotalSeconds,8:F3}] {logLevel,-11} {category}: {message}{(exception == null ? "" : " | " + exception.GetType().Name + ": " + exception.Message)}");

            if (logLevel < NexusLogLevel.Error || exception is null || IsExpected(exception))
                return;

            Interlocked.CompareExchange(ref _unexpected, new InvalidOperationException($"Server logged an unexpected {exception.GetType().Name}: {message}", exception), null);
        }

        public INexusLogger CreateLogger(string? pathSegment = null) => this;

        public void ThrowIfUnexpected()
        {
            var unexpected = Interlocked.Exchange(ref _unexpected, null);
            if (unexpected != null)
                throw unexpected;
        }

        private static bool IsExpected(Exception exception)
        {
            // The method invoker wraps failures from the invoked method.
            if (exception.InnerException is { } inner && exception.Message == "Exception occurred while running the method.")
                return IsExpected(inner);

            return exception
                is NexusSerializationException
                or OperationCanceledException
                or ObjectDisposedException
                // A method that takes a pipe or channel, invoked just before the connection drops: the session is
                // already closing, so the pipe can't be registered. Expected for an abrupt disconnect.
                || exception is InvalidOperationException { Message: "Can't register duplex pipe due to cancellation." };
        }
    }
}

public interface IFuzzServerNexus
{
    ValueTask Ping();
    ValueTask<int> Add(int a, int b);
    ValueTask TakeOrder(FuzzOrder order);
    ValueTask<FuzzOrder?> Echo(FuzzOrder? order);
    ValueTask Strings(string? single, string[]? many, Dictionary<string, int>? map);
    ValueTask Shape(IFuzzShape? shape);
    ValueTask<long[]?> Primitives(long[]? values, double[]? doubles, DateTime when, Guid id, decimal amount);
    ValueTask Cancellable(int value, CancellationToken cancellationToken);
    ValueTask Orders(INexusDuplexChannel<FuzzOrder> channel);
    ValueTask Ints(INexusDuplexChannel<int> channel);
    ValueTask Raw(INexusDuplexPipe pipe);
}

public interface IFuzzClientNexus
{
    ValueTask Notify(string? message);
}

[Nexus<IFuzzServerNexus, IFuzzClientNexus>(NexusType = NexusType.Server)]
public partial class FuzzServerNexus
{
    public ValueTask Ping() => default;
    public ValueTask<int> Add(int a, int b) => new(unchecked(a + b));
    public ValueTask TakeOrder(FuzzOrder order) => default;
    public ValueTask<FuzzOrder?> Echo(FuzzOrder? order) => new(order);
    public ValueTask Strings(string? single, string[]? many, Dictionary<string, int>? map) => default;
    public ValueTask Shape(IFuzzShape? shape) => default;
    public ValueTask<long[]?> Primitives(long[]? values, double[]? doubles, DateTime when, Guid id, decimal amount) => new(values);

    public async ValueTask Cancellable(int value, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(Math.Clamp(value, 0, 50), cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
    }

    public async ValueTask Orders(INexusDuplexChannel<FuzzOrder> channel)
    {
        await foreach (var _ in await channel.GetReaderAsync())
        {
        }
    }

    public async ValueTask Ints(INexusDuplexChannel<int> channel)
    {
        await foreach (var _ in await channel.GetReaderAsync())
        {
        }
    }

    public async ValueTask Raw(INexusDuplexPipe pipe)
    {
        while (true)
        {
            var result = await pipe.Input.ReadAsync();
            pipe.Input.AdvanceTo(result.Buffer.End);
            if (result.IsCompleted || result.IsCanceled)
                break;
        }
    }
}

[Nexus<IFuzzClientNexus, IFuzzServerNexus>(NexusType = NexusType.Client)]
public partial class FuzzClientNexus
{
    public ValueTask Notify(string? message) => default;
}
