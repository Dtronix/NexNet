using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Threading.Tasks;
using NexNet.Internals;
using NexNet.Logging;

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
    private int _nexusPipeHighWaterMark = 1024 * 192;
    private int _nexusPipeLowWaterMark = 1024 * 16;
    private int _nexusPipeHighWaterCutoff = 1024 * 256;
    private int _nexusPipeFlushChunkSize = 1024 * 8;

    /// <summary>
    /// Logger for the server/client.
    /// </summary>
    public INexusLogger? Logger { get; set; }

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
    /// Options to configure the receiving pipe with.
    /// </summary>
    public PipeOptions ReceiveSessionPipeOptions { get; set; } = PipeOptions.Default;

    /// <summary>
    /// The NexusPipe class will flush this maximum amount of data at once.
    /// If the data surpasses this limit, it will be divided into chunks of this size and sent until
    /// the entire data is transmitted. Must be between 1KB and 1MB.
    /// </summary>
    public virtual int NexusPipeFlushChunkSize
    {
        get => _nexusPipeFlushChunkSize;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1024, nameof(NexusPipeFlushChunkSize));
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 1024 * 1024, nameof(NexusPipeFlushChunkSize));
            _nexusPipeFlushChunkSize = value;
        }
    }

    /// <summary>
    /// Level at which the pipe will pause the writer.
    /// 192KB default. Must be between 1KB and 10MB.
    /// </summary>
    public int NexusPipeHighWaterMark
    {
        get => _nexusPipeHighWaterMark;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1024, nameof(NexusPipeHighWaterMark));
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 10 * 1024 * 1024, nameof(NexusPipeHighWaterMark));
            _nexusPipeHighWaterMark = value;
        }
    }

    /// <summary>
    /// Level at which the pipe will notify the other session to pause sending any more data until the amount
    /// of data buffered is this amount or less.
    /// 16KB default. Must be between 1KB and NexusPipeHighWaterMark.
    /// </summary>
    public int NexusPipeLowWaterMark
    {
        get => _nexusPipeLowWaterMark;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1024, nameof(NexusPipeLowWaterMark));
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, _nexusPipeHighWaterMark, nameof(NexusPipeLowWaterMark));
            _nexusPipeLowWaterMark = value;
        }
    }

    /// <summary>
    /// Level at which the pipe will stop the session from sending any more data until the low water mark is met.
    /// 256KB default. Must be between NexusPipeHighWaterMark and 100MB.
    /// </summary>
    public int NexusPipeHighWaterCutoff
    {
        get => _nexusPipeHighWaterCutoff;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, _nexusPipeHighWaterMark, nameof(NexusPipeHighWaterCutoff));
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 100 * 1024 * 1024, nameof(NexusPipeHighWaterCutoff));
            _nexusPipeHighWaterCutoff = value;
        }
    }



    internal Action<INexusSession, byte[]>? InternalOnSend;
    internal bool InternalOnSendSkipProtocolHeader;
    internal Func<INexusSession, ReadOnlySequence<byte>, ValueTask>? InternalOnReceive;
    internal Action<INexusSession>? InternalOnSessionSetup;
    internal bool InternalNoLingerOnShutdown = false;
    internal bool InternalForceDisableSendingDisconnectSignal = false;
}
