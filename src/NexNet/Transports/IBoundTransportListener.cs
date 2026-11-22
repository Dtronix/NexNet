using System.Net;

namespace NexNet.Transports;

/// <summary>
/// A listener bound to a socket end point. The server reads <see cref="BoundEndPoint"/> right after the listener is
/// created and reports it as <see cref="NexusEndpointInfo.BoundEndPoint"/>, so a listener configured with port 0
/// tells which port the operating system picked.
/// </summary>
internal interface IBoundTransportListener
{
    /// <summary>
    /// The end point the listening socket is bound to (<see cref="System.Net.Sockets.Socket.LocalEndPoint"/>, read
    /// after a successful bind); null when the platform does not report it.
    /// </summary>
    EndPoint? BoundEndPoint { get; }
}
