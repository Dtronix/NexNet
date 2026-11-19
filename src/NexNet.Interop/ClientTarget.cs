using NexNet.InteropServer;

namespace NexNet.Interop;

/// <summary>The transports a client under test connects over.</summary>
/// <remarks>The names match <see cref="InteropEndpointKind"/>, the endpoints of the interop server.</remarks>
public enum ClientTransport
{
    /// <summary>Plain TCP.</summary>
    Tcp,

    /// <summary>TLS over TCP.</summary>
    TcpTls,

    /// <summary>Unix domain socket.</summary>
    Uds,

    /// <summary>HttpSocket over plain HTTP.</summary>
    HttpSocket,

    /// <summary>HttpSocket over HTTPS.</summary>
    HttpSocketTls,

    /// <summary>WebSocket over plain HTTP (<c>ws://</c>).</summary>
    WebSocket,

    /// <summary>WebSocket over HTTPS (<c>wss://</c>).</summary>
    WebSocketTls,
}

/// <summary>Conversions and properties of <see cref="ClientTransport"/> values.</summary>
public static class ClientTransports
{
    /// <summary>Every transport, in declaration order.</summary>
    public static IReadOnlyList<ClientTransport> All { get; } = Enum.GetValues<ClientTransport>();

    /// <summary>The transports a <see cref="ClientMatrixAttribute"/> uses when it names none.</summary>
    public static IReadOnlyList<ClientTransport> Default { get; } = [ClientTransport.HttpSocketTls, ClientTransport.WebSocketTls];

    /// <summary>The interop server endpoint of <paramref name="transport"/>.</summary>
    public static InteropEndpointKind ToEndpointKind(this ClientTransport transport) => transport switch
    {
        ClientTransport.Tcp => InteropEndpointKind.Tcp,
        ClientTransport.TcpTls => InteropEndpointKind.TcpTls,
        ClientTransport.Uds => InteropEndpointKind.Uds,
        ClientTransport.HttpSocket => InteropEndpointKind.HttpSocket,
        ClientTransport.HttpSocketTls => InteropEndpointKind.HttpSocketTls,
        ClientTransport.WebSocket => InteropEndpointKind.WebSocket,
        ClientTransport.WebSocketTls => InteropEndpointKind.WebSocketTls,
        _ => throw new ArgumentOutOfRangeException(nameof(transport), transport, null),
    };

    /// <summary>True when the transport is encrypted (TLS, HTTPS, WSS).</summary>
    public static bool IsSecure(this ClientTransport transport)
        => transport is ClientTransport.TcpTls or ClientTransport.HttpSocketTls or ClientTransport.WebSocketTls;

    /// <summary>The capability a client needs for <paramref name="transport"/>, or <see cref="ClientCapabilities.None"/>.</summary>
    public static ClientCapabilities RequiredCapability(this ClientTransport transport) => transport switch
    {
        ClientTransport.Tcp or ClientTransport.TcpTls => ClientCapabilities.Tcp,
        ClientTransport.Uds => ClientCapabilities.Uds,
        ClientTransport.HttpSocket or ClientTransport.HttpSocketTls => ClientCapabilities.HttpSocket,
        _ => ClientCapabilities.None,
    };
}

/// <summary>
/// Optional features of a client implementation. A test declares what it needs
/// (<see cref="ClientMatrixAttribute.Requires"/>); rows of languages that lack it are visible skips.
/// </summary>
[Flags]
public enum ClientCapabilities
{
    /// <summary>Nothing optional.</summary>
    None = 0,

    /// <summary>Sends an HTTP <c>Authorization</c> header (HttpSocket/WebSocket).</summary>
    AuthorizationHeader = 1 << 0,

    /// <summary>Presents a TLS client certificate.</summary>
    ClientCertificate = 1 << 1,

    /// <summary>Pins the server's SPKI SHA-256.</summary>
    Pinning = 1 << 2,

    /// <summary>Trusts a custom root CA given as PEM.</summary>
    CustomCa = 1 << 3,

    /// <summary>Exposes the server certificate it received.</summary>
    RemoteCertificate = 1 << 4,

    /// <summary>Connects over HttpSocket.</summary>
    HttpSocket = 1 << 5,

    /// <summary>Connects over TCP and TLS over TCP.</summary>
    Tcp = 1 << 6,

    /// <summary>Connects over a Unix domain socket.</summary>
    Uds = 1 << 7,

    /// <summary>Connects over QUIC. Reserved: the interop server has no QUIC endpoint yet.</summary>
    Quic = 1 << 8,

    /// <summary>Runs its timers on a test clock (<see cref="IClientUnderTest.AdvanceTimeAsync"/>).</summary>
    FakeTime = 1 << 9,

    /// <summary>Provides a client pool.</summary>
    ClientPool = 1 << 10,
}

/// <summary>
/// One row of the client matrix: which client implementation (language) connects over which transport.
/// </summary>
/// <param name="Language">The language name (<see cref="ClientLanguages"/>).</param>
/// <param name="Transport">The transport.</param>
public sealed record ClientTarget(string Language, ClientTransport Transport)
{
    /// <summary>The C# in-process client over <paramref name="transport"/>.</summary>
    public static ClientTarget CSharp(ClientTransport transport) => new(ClientLanguages.CSharp, transport);

    /// <summary>
    /// Why this row cannot run (the language lacks the transport or a required capability); null when it can.
    /// <see cref="Harness"/> skips the test visibly when it is set.
    /// </summary>
    public string? SkipReason { get; init; }

    /// <summary>Skips the current test (TUnit <c>Skip.Test</c>) when <see cref="SkipReason"/> is set.</summary>
    public void SkipIfUnsupported()
    {
        if (SkipReason != null)
            Skip.Test(SkipReason);
    }

    /// <inheritdoc />
    public override string ToString() => Language + "/" + Transport;
}
