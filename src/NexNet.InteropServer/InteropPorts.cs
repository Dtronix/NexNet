using System.Net;
using System.Net.Sockets;

namespace NexNet.InteropServer;

/// <summary>
/// Hands out the loopback ports of the interop servers' TCP, TLS and QUIC listeners, unique within the process.
/// </summary>
/// <remarks>
/// <para>
/// A server keeps its ports across a stop and a restart, so a port must stay free while the server is stopped. Asking
/// the OS for a free port (bind to port 0, read it, close) gives a port from the ephemeral range, which the OS hands
/// out again to any client socket, including the clients of tests running in parallel; one that takes the port while
/// the server is stopped makes the restart fail with "address already in use" (H36). This allocator walks a range
/// below the ephemeral ranges of Windows (49152+), Linux (32768+) and macOS (49152+), so no client socket is ever
/// given one of its ports.
/// </para>
/// <para>
/// The range is disjoint from the integration tests' own allocator (<c>TestPorts</c>, 12000-31999), so the two never
/// hand out the same port in one process. The walk starts at a random offset so that test processes running at the
/// same time (other worktrees) are unlikely to walk the same ports, and skips ports that cannot be bound right now for
/// both TCP and UDP (held by another process, or excluded by the OS).
/// </para>
/// </remarks>
internal static class InteropPorts
{
    internal const int First = 4000;
    internal const int Count = 8000; // up to 11999

    private static int _next = Random.Shared.Next(Count);

    /// <summary>
    /// A port that is free on the loopback address for both TCP and UDP, and that no other caller in this process has
    /// been given (until the range wraps around).
    /// </summary>
    public static int Next()
    {
        for (var attempt = 0; attempt < Count; attempt++)
        {
            var port = First + (int)((uint)Interlocked.Increment(ref _next) % Count);
            if (IsFree(port))
                return port;
        }

        throw new InvalidOperationException($"No free loopback port in {First}-{First + Count - 1}.");
    }

    private static bool IsFree(int port)
    {
        var endPoint = new IPEndPoint(IPAddress.Loopback, port);
        try
        {
            using var tcp = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            using var udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

            // Windows lets a socket bind over another's wildcard binding unless it asks for exclusive use; other
            // platforms refuse an overlapping bind by default.
            if (OperatingSystem.IsWindows())
            {
                tcp.ExclusiveAddressUse = true;
                udp.ExclusiveAddressUse = true;
            }

            tcp.Bind(endPoint);
            udp.Bind(endPoint);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }
}
