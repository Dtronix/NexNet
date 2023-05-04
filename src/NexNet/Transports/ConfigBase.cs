using System;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using NexNet.Internals;

namespace NexNet.Transports;

public abstract class ConfigBase
{
    public INexNetLogger? Logger { get; set; }

    private long _sessionCounter = 0;

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


    internal EndPoint SocketEndPoint { get; init; }
    internal AddressFamily SocketAddressFamily { get; init; }
    internal SocketType SocketType { get; init; }
    internal ProtocolType SocketProtocolType { get; init; }

    public PipeOptions SendPipeOptions { get; set; } = PipeOptions.Default;
    public PipeOptions ReceivePipeOptions { get; set; } = PipeOptions.Default;

    internal Action<INexNetSession, byte[]>? InternalOnSend;
    internal Action<INexNetSession>? InternalOnSessionSetup;
    internal bool InternalNoLingerOnShutdown = false;
    internal bool InternalForceDisableSendingDisconnectSignal = false;


    public long GetNewSessionId()
    {
        return Interlocked.Increment(ref _sessionCounter);
    }
}
