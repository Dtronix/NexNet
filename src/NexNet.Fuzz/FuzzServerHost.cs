using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipelines;
using System.Threading.Channels;
using NexNet.Logging;
using NexNet.Messages;
using NexNet.Serialization;
using NexNet.Transports;

namespace NexNet.Fuzz;

/// <summary>
/// A long-lived NexNet server that accepts in-memory transports. Shared by the session-level harnesses.
/// </summary>
internal sealed class FuzzServerHost
{
    public static readonly byte[] Preamble = [0x4E, 0x6E, 0x50, 0x14, 0, 0, 0, 2];

    private readonly FuzzListener _listener = new();
    public FuzzLogger Logger { get; } = new();
    public byte[] Greeting { get; private set; } = [];

    /// <summary>
    /// Starts a server.
    /// </summary>
    /// <param name="start">Creates and starts the server nexus with the given config.</param>
    /// <param name="serverNexusHash">Method hash of the server nexus, sent in the client greeting.</param>
    /// <param name="clientNexusHash">Method hash of the client nexus, sent in the client greeting.</param>
    /// <param name="configure">Optional changes to the default config.</param>
    public static FuzzServerHost Start(Func<ServerConfig, Task> start, int serverNexusHash, int clientNexusHash, Action<ServerConfig>? configure = null)
    {
        var host = new FuzzServerHost();
        // No linger after sending a disconnect message: it only adds wall time per input.
        // Short idle timeout: a read loop held up inside a pipe write can't see the stream end, so only the idle
        // timeout closes it. 2 s (checked every Timeout / 4) keeps that inside the harness timeout; a session still
        // open after it is a real hang.
        var config = new FuzzServerConfig(host._listener) { Logger = host.Logger, DisconnectDelay = 0, Timeout = 2000 };
        configure?.Invoke(config);
        start(config).GetAwaiter().GetResult();

        var greeting = new ClientGreetingMessage
        {
            Version = null,
            ServerNexusHash = serverNexusHash,
            ClientNexusHash = clientNexusHash,
        };
        host.Greeting = FrameMessage(MessageType.ClientGreeting, greeting);
        return host;
    }

    public void Accept(FuzzTransport transport) => _listener.Pending.Writer.TryWrite(transport);

    public static byte[] FrameMessage<T>(MessageType type, T message)
        where T : IMessageBase
    {
        var body = new ArrayBufferWriter<byte>();
        var writer = new MsgPackWriter(body);
        message.Serialize(ref writer);
        writer.Flush();
        return Frame(type, body.WrittenSpan);
    }

    public static byte[] Frame(MessageType type, ReadOnlySpan<byte> body)
    {
        var frame = new byte[3 + body.Length];
        frame[0] = (byte)type;
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(1), checked((ushort)body.Length));
        body.CopyTo(frame.AsSpan(3));
        return frame;
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
}

internal sealed class FuzzTransport : ITransport
{
    // No back pressure: the harness may never read what the server sends, so writes must not block.
    private static readonly PipeOptions Options = new(pauseWriterThreshold: 0, resumeWriterThreshold: 0, useSynchronizationContext: false);

    private readonly Pipe _serverToClient = new(Options);
    private readonly bool _readsOutput;

    public readonly Pipe ClientToServer = new(Options);
    public ManualResetEventSlim Closed { get; } = new();
    public PipeReader Input => ClientToServer.Reader;
    public PipeWriter Output => _serverToClient.Writer;

    /// <summary>
    /// What the server sent. Only for a transport created with <c>readsOutput</c>; the caller must read it to the end.
    /// </summary>
    public PipeReader ServerOutput => _serverToClient.Reader;

    public string? RemoteAddress => "fuzz";
    public int? RemotePort => null;

    /// <param name="readsOutput">True if the harness reads <see cref="ServerOutput"/>. Otherwise the output is
    /// discarded when the transport closes.</param>
    public FuzzTransport(bool readsOutput = false)
    {
        _readsOutput = readsOutput;
    }

    public ValueTask CloseAsync(bool linger)
    {
        _serverToClient.Writer.Complete();
        // A reading harness completes the reader itself; completing it here would race with its ReadAsync.
        if (!_readsOutput)
            _serverToClient.Reader.Complete();
        Closed.Set();
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
            or NexusBusyException
            or OperationCanceledException
            or ObjectDisposedException
            // A method that takes a pipe or channel, invoked just before the connection drops: the session is
            // already closing, so the pipe can't be registered. Expected for an abrupt disconnect.
            || exception is InvalidOperationException { Message: "Can't register duplex pipe due to cancellation." };
    }
}
