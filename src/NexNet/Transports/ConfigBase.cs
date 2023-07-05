using System;
using System.IO.Pipelines;
using NexNet.Internals;

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
    /// If a client has not sent any message within this time frame,
    /// including a ping, the client will be disconnected.
    /// </summary>
    public int Timeout { get; set; } = 30_000;

    /*
    internal EndPoint SocketEndPoint { get; init; }
    internal AddressFamily SocketAddressFamily { get; init; }
    internal SocketType SocketType { get; init; }
    internal ProtocolType SocketProtocolType { get; init; }*/

    /// <summary>
    /// Options to configure the sending pipe with.
    /// </summary>
    public PipeOptions SendPipeOptions { get; set; }  = PipeOptions.Default;/*= new PipeOptions(
        pauseWriterThreshold: ushort.MaxValue,
        resumeWriterThreshold: ushort.MaxValue / 2,
        minimumSegmentSize: ushort.MaxValue);*/

    /// <summary>
    /// Options to configure the receiving pipe with.
    /// </summary>
    public PipeOptions ReceivePipeOptions { get; set; } = PipeOptions.Default;

    internal Action<INexusSession, byte[]>? InternalOnSend;
    internal Action<INexusSession>? InternalOnSessionSetup;
    internal bool InternalNoLingerOnShutdown = false;
    internal bool InternalForceDisableSendingDisconnectSignal = false;
}
