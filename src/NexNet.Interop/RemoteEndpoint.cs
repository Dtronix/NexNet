namespace NexNet.Interop;

/// <summary>
/// The server's address and port as a client's current connection reports them (C# <c>ISessionContext.RemoteAddress</c> /
/// <c>RemotePort</c>, Java <c>ClientSessionContext.remoteAddress()</c> / <c>remotePort()</c>, JS
/// <c>NexusClient.remoteAddress</c> / <c>remotePort</c>). See <see cref="IClientUnderTest.GetRemoteEndpointAsync"/>.
/// </summary>
/// <param name="Address">The address (an IP address in text form, or the socket path of a Unix domain socket); null when the transport does not report one.</param>
/// <param name="Port">The port; null when the transport does not report one (a Unix domain socket has none).</param>
public sealed record RemoteEndpoint(string? Address, int? Port)
{
    /// <summary>Nothing reported: the client is not connected, or its transport does not expose the remote endpoint.</summary>
    public static RemoteEndpoint None { get; } = new(null, null);
}
