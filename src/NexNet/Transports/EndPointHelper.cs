using System.Net;
using System.Net.Sockets;

namespace NexNet.Transports;

/// <summary>
/// Helper class for extracting address and port information from endpoints.
/// </summary>
internal static class EndPointHelper
{
    /// <summary>
    /// Extracts address and port information from an EndPoint.
    /// </summary>
    /// <param name="endPoint">The endpoint to extract information from.</param>
    /// <returns>A tuple containing the address (string) and port (int?).</returns>
    public static (string? Address, int? Port) ExtractEndPointInfo(EndPoint? endPoint)
    {
        return endPoint switch
        {
            IPEndPoint ipEndPoint => (ipEndPoint.Address.ToString(), ipEndPoint.Port),
            UnixDomainSocketEndPoint udsEndPoint => (udsEndPoint.ToString(), null),
            DnsEndPoint dnsEndPoint => (dnsEndPoint.Host, dnsEndPoint.Port),
            _ => (endPoint?.ToString(), null)
        };
    }
}
