using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Threading.Tasks;
using NexNet.Internals;
using NexNet.Logging;
using NexNet.Serialization;

namespace NexNet.Transports;

/// <summary>
/// Base configuration for servers and clients.
/// </summary>
public abstract class ConfigBase
{
    // Backing fields for validated properties
    private int _maxConcurrentConnectionInvocations = 2;
    private int _disconnectDelay = 200;
    private int _timeout = 30_000;
    private int _handshakeTimeout = 15_000;
    private int? _sessionBudget;
    private int? _maxPipes;
    private int? _pipeWindow;
    private int? _maxMessageSize;
    private int? _invocationCredits;
    private int _progressTimeout = 30_000;
    private PipeOptions _receiveSessionPipeOptions = CreateReceivePipeOptions();

    /// <summary>
    /// Smallest pause threshold for a transport's receive pipe: two maximum frames. A frame is only consumed once it
    /// is complete, so a pipe that pauses below one frame can never resume.
    /// </summary>
    internal const long MinReceivePauseThreshold = 2 * (ushort.MaxValue + 5);

    /// <summary>
    /// Logger for the server/client.
    /// </summary>
    public INexusLogger? Logger { get; set; }

    /// <summary>
    /// Time source for all time-dependent behavior on this server/client (timers, delays,
    /// timeouts, cache expiry). Defaults to <see cref="TimeProvider.System"/>. Tests can
    /// substitute <c>FakeTimeProvider</c> from <c>Microsoft.Extensions.TimeProvider.Testing</c>
    /// to drive time deterministically.
    /// </summary>
    /// <remarks>
    /// Set this before invoking any lifecycle method (<c>StartAsync</c>, <c>ConnectAsync</c>,
    /// <c>Configure</c>). Mutation after that point is supported but limited: already-stored
    /// <see cref="DateTime"/>/tick values (e.g. <c>NexusClientPool</c> last-used timestamps),
    /// already-constructed timers, and in-flight <see cref="Task.Delay(TimeSpan, TimeProvider)"/>
    /// continuations retain the provider that was active when they were created.
    /// </remarks>
    public TimeProvider Time { get; set; } = TimeProvider.System;

    private NexusSerializerOptions _serializerOptions = NexusSerializerOptions.Untrusted;
    private NexusSerializerOptions? _resolvedSerializerOptions;

    /// <summary>
    /// Options applied when deserializing data received from the remote peer.
    /// Defaults to <see cref="NexusSerializerOptions.Untrusted"/> (hardening for hostile peers). An unset
    /// <see cref="NexusSerializerOptions.MaxElements"/> reads back as <c>max(65536, MaxMessageSize / 16)</c>.
    /// </summary>
    public NexusSerializerOptions SerializerOptions
    {
        get
        {
            if (_serializerOptions.MaxElements != 0)
                return _serializerOptions;

            var maxElements = Math.Max(65536, MaxMessageSize / 16);
            var resolved = _resolvedSerializerOptions;
            if (resolved == null || resolved.MaxElements != maxElements)
                _resolvedSerializerOptions = resolved = _serializerOptions with { MaxElements = maxElements };
            return resolved;
        }
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            _serializerOptions = value;
            _resolvedSerializerOptions = null;
        }
    }

    /// <summary>
    /// The maximum number of concurrent invocations which can occur from a single connection.
    /// Must be between 1 and 1000.
    /// </summary>
    public int MaxConcurrentConnectionInvocations
    {
        get => _maxConcurrentConnectionInvocations;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1, nameof(MaxConcurrentConnectionInvocations));
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 1000, nameof(MaxConcurrentConnectionInvocations));
            _maxConcurrentConnectionInvocations = value;
        }
    }

    /// <summary>
    /// This delay is added at the end of connections to ensure that the disconnect message has time to send.
    /// Values less than 1 disable this functionality. Maximum value is 10000 (10 seconds).
    /// </summary>
    public int DisconnectDelay
    {
        get => _disconnectDelay;
        set
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 10_000, nameof(DisconnectDelay));
            _disconnectDelay = value;
        }
    }


    /// <summary>
    /// The time in milliseconds if a connection has not sent any message within this time frame,
    /// including a ping, the client will be disconnected.
    /// Must be between 50ms and 300000ms (5 minutes). Values below 1000ms are not recommended for production.
    /// </summary>
    public int Timeout
    {
        get => _timeout;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 50, nameof(Timeout));
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 300_000, nameof(Timeout));
            _timeout = value;
        }
    }

    /// <summary>
    /// If a full "HelloMessage" hasn't been received within this time the connection will be terminated.
    /// Must be between 50ms and 60000ms (1 minute). Values below 1000ms are not recommended for production.
    /// </summary>
    public int HandshakeTimeout
    {
        get => _handshakeTimeout;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 50, nameof(HandshakeTimeout));
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 60_000, nameof(HandshakeTimeout));
            _handshakeTimeout = value;
        }
    }

    /*
    internal EndPoint SocketEndPoint { get; init; }
    internal AddressFamily SocketAddressFamily { get; init; }
    internal SocketType SocketType { get; init; }
    internal ProtocolType SocketProtocolType { get; init; }*/

    /// <summary>
    /// Options to configure the sending pipe with.
    /// </summary>
    public PipeOptions SendSessionPipeOptions { get; set; }  = PipeOptions.Default;/*= new PipeOptions(
        pauseWriterThreshold: ushort.MaxValue,
        resumeWriterThreshold: ushort.MaxValue / 2,
        minimumSegmentSize: ushort.MaxValue);*/

    /// <summary>
    /// Options to configure the receiving pipe with. Defaults to pausing at 256KB and resuming at 128KB.
    /// The pause threshold must be 0 (never pause) or at least two maximum frames (131,080 bytes).
    /// </summary>
    public PipeOptions ReceiveSessionPipeOptions
    {
        get => _receiveSessionPipeOptions;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            ValidateReceivePipeOptions(value, nameof(ReceiveSessionPipeOptions));
            _receiveSessionPipeOptions = value;
        }
    }

    /// <summary>
    /// Default options for a transport's receive pipe.
    /// </summary>
    internal static PipeOptions CreateReceivePipeOptions(bool useSynchronizationContext = true)
        => new(pauseWriterThreshold: 256 * 1024, resumeWriterThreshold: 128 * 1024, useSynchronizationContext: useSynchronizationContext);

    internal static void ValidateReceivePipeOptions(PipeOptions options, string paramName)
    {
        if (options.PauseWriterThreshold != 0 && options.PauseWriterThreshold < MinReceivePauseThreshold)
            throw new ArgumentOutOfRangeException(paramName, options.PauseWriterThreshold,
                $"The pause threshold must be 0 or at least {MinReceivePauseThreshold} bytes.");
    }

    // Reads the options as set: the SerializerOptions getter depends on MaxMessageSize, which depends on this.
    private bool IsTrusted => _serializerOptions.Security == SerializerSecurity.Trusted;

    /// <summary>
    /// Receive memory per session, in bytes. Every pipe window and every large-value reservation is drawn from it.
    /// Defaults to 16MB, or 64MB with <see cref="SerializerSecurity.Trusted"/> serializer options. Must be between
    /// 64KB and 1GB.
    /// </summary>
    public int SessionBudget
    {
        get => _sessionBudget ?? (IsTrusted ? 64 * 1024 * 1024 : 16 * 1024 * 1024);
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 64 * 1024, nameof(SessionBudget));
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 1024 * 1024 * 1024, nameof(SessionBudget));
            _sessionBudget = value;
        }
    }

    /// <summary>
    /// Most pipes (including channels and collection connections) open on a session at once. Opening one more is
    /// refused. 32 default. Must be between 1 and 255.
    /// </summary>
    public int MaxPipes
    {
        get => _maxPipes ?? 32;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1, nameof(MaxPipes));
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 255, nameof(MaxPipes));
            _maxPipes = value;
        }
    }

    /// <summary>
    /// Bytes a peer may send on a pipe before this side has read them: the pipe's receive window, reserved from
    /// <see cref="SessionBudget"/> while the pipe is open. Defaults to 256KB, or 1MB with
    /// <see cref="SerializerSecurity.Trusted"/> serializer options. Must be between 4KB and 1GB.
    /// </summary>
    public int PipeWindow
    {
        get => _pipeWindow ?? (IsTrusted ? 1024 * 1024 : 256 * 1024);
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 4 * 1024, nameof(PipeWindow));
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 1024 * 1024 * 1024, nameof(PipeWindow));
            _pipeWindow = value;
        }
    }

    /// <summary>
    /// Largest argument, result or channel item accepted, in bytes. Defaults to 4MB, or 16MB with
    /// <see cref="SerializerSecurity.Trusted"/> serializer options. Must be between 64KB and 1GB.
    /// </summary>
    public int MaxMessageSize
    {
        get => _maxMessageSize ?? (IsTrusted ? 16 * 1024 * 1024 : 4 * 1024 * 1024);
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 64 * 1024, nameof(MaxMessageSize));
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 1024 * 1024 * 1024, nameof(MaxMessageSize));
            _maxMessageSize = value;
        }
    }

    /// <summary>
    /// Invocations the peer may have queued on this side at once. Defaults to 32, or 128 with
    /// <see cref="SerializerSecurity.Trusted"/> serializer options. Must be between 1 and 10,000.
    /// </summary>
    public int InvocationCredits
    {
        get => _invocationCredits ?? (IsTrusted ? 128 : 32);
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1, nameof(InvocationCredits));
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 10_000, nameof(InvocationCredits));
            _invocationCredits = value;
        }
    }

    /// <summary>
    /// Longest time without progress before the session disconnects with
    /// <see cref="NexNet.Messages.DisconnectReason.Stalled"/>, in milliseconds. Progress is measured on a pipe that is closing (until
    /// the peer confirms the close), on an inbound large argument or result and on a channel item given extra credit
    /// (since their last data), and on an outbound transport flush. Waiting for credit is never a stall.
    /// 30000 default. Must be between 50 and 600000.
    /// </summary>
    public int ProgressTimeout
    {
        get => _progressTimeout;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 50, nameof(ProgressTimeout));
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 600_000, nameof(ProgressTimeout));
            _progressTimeout = value;
        }
    }

    /// <summary>
    /// Throws if <see cref="SessionBudget"/> can't hold the fixed reservations:
    /// <c>MaxPipes × PipeWindow + MaxMessageSize + InvocationCredits × 64KB</c>.
    /// </summary>
    internal void ValidateFlowControl()
    {
        var required = (long)MaxPipes * PipeWindow + MaxMessageSize + (long)InvocationCredits * 64 * 1024;
        if (required > SessionBudget)
            throw new InvalidOperationException(
                $"{nameof(SessionBudget)} ({SessionBudget} bytes) is smaller than {nameof(MaxPipes)} x {nameof(PipeWindow)} + " +
                $"{nameof(MaxMessageSize)} + {nameof(InvocationCredits)} x 64KB ({required} bytes).");
    }



    internal Action<INexusSession, byte[]>? InternalOnSend;
    internal bool InternalOnSendSkipProtocolHeader;
    internal Func<INexusSession, ReadOnlySequence<byte>, ValueTask>? InternalOnReceive;
    internal Action<INexusSession>? InternalOnSessionSetup;
    internal bool InternalNoLingerOnShutdown = false;
    internal bool InternalForceDisableSendingDisconnectSignal = false;
}
