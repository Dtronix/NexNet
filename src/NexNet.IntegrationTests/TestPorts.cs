using System.Net;
using System.Net.Sockets;

namespace NexNet.IntegrationTests;

/// <summary>
/// Hands out loopback ports for test servers, unique within the test process.
/// </summary>
/// <remarks>
/// Asking the OS for a free port (bind to port 0, read it, close) races: the OS hands the same ephemeral port to the
/// next caller, including the client sockets of tests running in parallel, before the server binds it. This allocator
/// instead walks a range below the ephemeral ranges of Windows (49152+) and Linux (32768+), so no client socket is ever
/// given one of its ports. It starts at a random offset so that test processes running at the same time (other
/// worktrees) are unlikely to walk the same ports, never hands a port out twice in one process, and skips ports that
/// cannot be bound right now for both TCP and UDP (held by another process, or excluded by the OS).
/// </remarks>
internal static class TestPorts
{
    private const int First = 12000;
    private const int Count = 20000; // up to 31999

    private static int _next = Random.Shared.Next(Count);

    /// <summary>
    /// A port that is free on the loopback address for both TCP and UDP, and that no other caller in this process has
    /// been given.
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
