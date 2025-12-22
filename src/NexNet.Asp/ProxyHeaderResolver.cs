using System;
using System.Linq;
using Microsoft.AspNetCore.Http;

namespace NexNet.Asp;

/// <summary>
/// Resolves the real client IP address from proxy headers.
/// </summary>
public static class ProxyHeaderResolver
{
    /// <summary>
    /// Header names checked in order of priority.
    /// </summary>
    private static readonly string[] ForwardedHeaders =
    {
        "X-Forwarded-For",
        "X-Real-IP",
        "CF-Connecting-IP",      // Cloudflare
        "True-Client-IP",        // Akamai
        "X-Client-IP"
    };

    /// <summary>
    /// Extracts the remote client address and port from the HttpContext,
    /// checking proxy headers first, then falling back to the connection info.
    /// </summary>
    /// <param name="context">The HTTP context.</param>
    /// <param name="trustProxyHeaders">Whether to trust and use proxy headers.</param>
    /// <returns>A tuple of (address, port).</returns>
    public static (string? Address, int? Port) GetRemoteEndPoint(HttpContext context, bool trustProxyHeaders)
    {
        string? address = null;
        int? port = null;

        if (trustProxyHeaders)
        {
            // Try to get the address from proxy headers
            foreach (var headerName in ForwardedHeaders)
            {
                var headerValue = context.Request.Headers[headerName].FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(headerValue))
                {
                    // X-Forwarded-For can contain multiple IPs: "client, proxy1, proxy2"
                    // The first one is the original client
                    var firstIp = headerValue.Split(',', StringSplitOptions.RemoveEmptyEntries)
                        .FirstOrDefault()?.Trim();

                    if (!string.IsNullOrWhiteSpace(firstIp))
                    {
                        // Handle IPv6 addresses with port: [::1]:1234
                        if (firstIp.StartsWith('['))
                        {
                            var closeBracket = firstIp.IndexOf(']');
                            if (closeBracket > 0)
                            {
                                address = firstIp.Substring(1, closeBracket - 1);
                                var portPart = firstIp.Substring(closeBracket + 1);
                                if (portPart.StartsWith(':') && int.TryParse(portPart.Substring(1), out var parsedPort))
                                {
                                    port = parsedPort;
                                }
                            }
                        }
                        // Handle IPv4 with port: 192.168.1.1:1234
                        else if (firstIp.Contains(':') && !firstIp.Contains('.'))
                        {
                            // This is likely an IPv6 address without brackets
                            address = firstIp;
                        }
                        else if (firstIp.Contains(':'))
                        {
                            var parts = firstIp.Split(':');
                            address = parts[0];
                            if (parts.Length > 1 && int.TryParse(parts[1], out var parsedPort))
                            {
                                port = parsedPort;
                            }
                        }
                        else
                        {
                            address = firstIp;
                        }
                        break;
                    }
                }
            }

            // Try X-Forwarded-Port header
            if (port == null)
            {
                var forwardedPort = context.Request.Headers["X-Forwarded-Port"].FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(forwardedPort) && int.TryParse(forwardedPort, out var parsedPort))
                {
                    port = parsedPort;
                }
            }
        }

        // Fall back to connection info if no proxy headers found
        if (string.IsNullOrWhiteSpace(address))
        {
            address = context.Connection.RemoteIpAddress?.ToString();
            port = context.Connection.RemotePort;
        }

        return (address, port);
    }
}
