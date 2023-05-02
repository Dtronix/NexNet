using System;
using System.IO.Pipelines;
using System.Net.Sockets;

namespace NexNet.Transports;

public interface ITransportBase : IDuplexPipe, IDisposable
{
    public Socket Socket { get; }
}
