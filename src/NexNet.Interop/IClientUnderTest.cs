using NexNet.Messages;

namespace NexNet.Interop;

/// <summary>
/// One NexNet client driven by a test, whatever its language: the C# client in process
/// (<see cref="InProc.InProcClientDriver{TClientNexus, TServerProxy}"/>) or a client in a language host (H05). Every
/// member is asynchronous, because a remote client's state is only known after a round trip.
/// </summary>
/// <remarks>
/// <para>Errors are rethrown as the C# exceptions of the <see cref="ErrorKind"/> they map to (<see cref="ErrorKinds"/>).</para>
/// <para>Timeouts are in seconds, already scaled by the caller.</para>
/// </remarks>
public interface IClientUnderTest : IAsyncDisposable
{
    /// <summary>The language and transport of this client.</summary>
    ClientTarget Target { get; }

    /// <summary>What this client supports.</summary>
    ClientCapabilities Capabilities { get; }

    /// <summary>Connects; throws when the connection fails.</summary>
    Task ConnectAsync();

    /// <summary>Connects; false when the connection fails.</summary>
    Task<bool> TryConnectAsync();

    /// <summary>Disconnects gracefully.</summary>
    Task DisconnectAsync();

    /// <summary>The current connection state.</summary>
    Task<ConnectionState> GetStateAsync();

    /// <summary>
    /// The certificate the server presented on the client's current connection, as DER bytes: null when the client is not
    /// connected or its transport has no TLS. The browser cannot read the peer certificate
    /// (<see cref="ClientCapabilities.RemoteCertificate"/> is absent there).
    /// </summary>
    /// <exception cref="NotSupportedException">The client is a pool rental.</exception>
    Task<byte[]?> GetRemoteCertificateAsync();

    /// <summary>Waits until the client reaches state <paramref name="state"/>.</summary>
    /// <exception cref="TimeoutException">Not reached within <paramref name="seconds"/>.</exception>
    Task WaitForStateAsync(ConnectionState state, double seconds);

    /// <summary>Waits until the client's session disconnects and returns why.</summary>
    /// <exception cref="TimeoutException">Not disconnected within <paramref name="seconds"/>.</exception>
    Task<DisconnectReason> WhenDisconnectedAsync(double seconds);

    /// <summary>Waits until the client is connected and ready for invocations.</summary>
    /// <exception cref="TimeoutException">Not ready within <paramref name="seconds"/>.</exception>
    Task WhenReadyAsync(double seconds);

    /// <summary>
    /// The proxy of the server nexus. <typeparamref name="TProxy"/> is a generated remote proxy (<c>Remote_IServerNexus</c>,
    /// H06) or a server nexus interface it implements. One instance per type.
    /// </summary>
    TProxy Proxy<TProxy>();

    /// <summary>
    /// The script facade (<c>&lt;ClientNexus&gt;Script</c>, H06) that decides what this client does when the server invokes
    /// its client nexus. One instance per client.
    /// </summary>
    TScript Callbacks<TScript>();

    /// <summary>Creates a duplex pipe to pass to a server method.</summary>
    Task<IRemotePipe> CreatePipeAsync();

    /// <summary>
    /// The pipe a <c>pipe.hold</c> script step of this client published under <paramref name="name"/> (control protocol
    /// §6.9): a pipe the server opened, passed to a client nexus method, that the test then drives like one of its own.
    /// Waits until the step runs. The method stays running until the pipe is disposed, which releases it to the client
    /// runtime instead of disposing it.
    /// </summary>
    /// <exception cref="NotSupportedException">The client is not scripted (a pooled client).</exception>
    Task<IRemotePipe> AcceptPipeAsync(string name)
        => throw new NotSupportedException($"{Target}: this client has no scripted client nexus to hold pipes.");

    /// <summary>The client view of the server nexus collection named <paramref name="collectionName"/>.</summary>
    IRemoteList<T> List<T>(string collectionName);

    /// <summary>Raises the signal <paramref name="name"/> that script steps wait for (<see cref="AwaitSignalStep"/>). Signals stay raised.</summary>
    Task SignalAsync(string name);

    /// <summary>
    /// Completes once every operation started on this client before it has been handed to the client runtime (fire and
    /// forget invocations sent, control requests processed).
    /// </summary>
    Task BarrierAsync();

    /// <summary>Moves the client's test clock (<see cref="ClientSpec.FakeTime"/>) forward.</summary>
    /// <exception cref="InvalidOperationException">The client was not created with <see cref="ClientSpec.FakeTime"/>.</exception>
    Task AdvanceTimeAsync(TimeSpan by);

    /// <summary>Reports what this client still holds; called after each test.</summary>
    Task<LeakReport> EndTestAuditAsync();
}
