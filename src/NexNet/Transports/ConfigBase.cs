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
    /// <summary>
    /// Logger for the server/client.
    /// </summary>
    public INexusLogger? Logger { get; set; }

    /// <summary>
    /// The maximum number of concurrent invocations which can occur from a single connection.
    /// </summary>
    public int MaxConcurrentConnectionInvocations { get; set; } = 2;

    /// <summary>
    /// This delay is added at the end of connections to ensure that the disconnect message has time to send.
    /// Values less than 1 disable this functionality.
    /// </summary>
    public int DisconnectDelay { get; set; } = 200;


    /// <summary>
    /// The time in milliseconds if a connection has not sent any message within this time frame,
    /// including a ping, the client will be disconnected.
    /// </summary>
    public int Timeout { get; set; } = 30_000;
    
    /// <summary>
    /// If a full "HelloMessage" hasn't been received within this time the connection will be terminated.
    /// </summary>
    public int HandshakeTimeout { get; set; } = 15_000;

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
    /// If the data surpasses this limit, it will be divided into chunks of this ize and sent until
    /// the entire data is transmitted.
    /// </summary>
    public virtual int NexusPipeFlushChunkSize { get; set; } = 1024 * 8;

    /// <summary>
    /// Level at which the pipe will pause the writer.
    /// 192KB default.
    /// </summary>
    public int NexusPipeHighWaterMark { get; set; } = 1024 * 192;

    /// <summary>
    /// Level at which the pipe will notify the other session to pause sending any more data until the amount
    /// of data buffered is this amount or less.
    /// 64KB default.
    /// </summary>
    public int NexusPipeLowWaterMark { get; set; } = 1024 * 16;

    /// <summary>
    /// Level at which the pipe will stop the session from sending any more data until the low water mark is met.
    /// 1MB default.
    /// </summary>
    public int NexusPipeHighWaterCutoff { get; set; } = 1024 * 256;



    internal Action<INexusSession, byte[]>? InternalOnSend;
    internal bool InternalOnSendSkipProtocolHeader;
    internal Func<INexusSession, ReadOnlySequence<byte>, ValueTask>? InternalOnReceive;
    internal Action<INexusSession>? InternalOnSessionSetup;
    internal bool InternalNoLingerOnShutdown = false;
    internal bool InternalForceDisableSendingDisconnectSignal = false;
}
