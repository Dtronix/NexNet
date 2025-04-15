using System;
using System.Threading.Tasks;
using NexNet.Transports.HttpSocket;

namespace NexNet.Asp.HttpSocket;

/// <summary>
/// Feature to transition the request to a HttpSocket pipe.
/// </summary>
public interface IHttpSocketFeature
{
    
    /// <summary>
    /// Returns true if the connection is a HttpSocket request.
    /// </summary>
    bool IsHttpSocketRequest { get; }
    
    /// <summary>
    /// Accepts the duplex socket for handing off to a Nexus Server.
    /// </summary>
    /// <returns>Duplex socket upon successfully accepting.</returns>
    /// <exception cref="InvalidOperationException">Throws if the connection is not a HttpSocket.</exception>
    Task<HttpSocketDuplexPipe> AcceptAsync();
}
