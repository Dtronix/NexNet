using NexNet.Collections;
using NexNet.Invocation;

namespace NexNet.Interop;

/// <summary>
/// One client pool driven by a test, whatever its language: the C# <see cref="NexusClientPool{TClientNexus, TServerProxy}"/>
/// in process (<see cref="InProc.InProcPoolDriver{TClientNexus, TServerProxy}"/>) or the pool of a language host
/// (<see cref="Remote.RemotePoolDriver"/>, control protocol §6.11). Needs <see cref="ClientCapabilities.ClientPool"/>.
/// </summary>
/// <remarks>
/// <para>Disposing the pool disposes its idle clients. Rentals not returned stay connected until they are returned, as
/// in the C# pool, so a test returns every rental (<c>await using</c>) and disposes the pool.</para>
/// <para>The pool and its clients are covered by the leak audit: <see cref="EndTestAuditAsync"/>, and on a language
/// host the end of the test scope.</para>
/// </remarks>
public interface IPoolUnderTest : IAsyncDisposable
{
    /// <summary>The language and transport of the pool's clients.</summary>
    ClientTarget Target { get; }

    /// <summary>What the pool's clients support.</summary>
    ClientCapabilities Capabilities { get; }

    /// <summary>
    /// Rents a connected client: an idle one when the pool holds a healthy one (unhealthy idle clients are disposed on
    /// the way), otherwise a new one, connected before it is returned. Waits while
    /// <see cref="ClientPoolSpec.MaxConnections"/> clients are rented.
    /// </summary>
    /// <param name="cancellationToken">Cancels the wait for a free slot, and the connection attempt.</param>
    /// <exception cref="ClientPoolConnectionException">
    /// The new client could not connect (its <see cref="ClientPoolConnectionException.ConnectionResult"/> says why, e.g.
    /// <see cref="Messages.DisconnectReason.Authentication"/>). A cancel during the connection attempt ends this way too.
    /// </exception>
    /// <exception cref="OperationCanceledException">Canceled while waiting for a free slot.</exception>
    /// <exception cref="ObjectDisposedException">The pool is disposed.</exception>
    Task<IRentedClientUnderTest> RentAsync(CancellationToken cancellationToken = default);

    /// <summary>The pool's counts.</summary>
    Task<ClientPoolState> GetStateAsync();

    /// <summary>
    /// Moves the test clock of the pool (<see cref="ClientSpec.FakeTime"/>) forward: the health check and the timers of
    /// every client of the pool, which share it. Timers due fire before this completes.
    /// </summary>
    /// <exception cref="InvalidOperationException">The pool was not created with <see cref="ClientSpec.FakeTime"/>.</exception>
    Task AdvanceTimeAsync(TimeSpan by);

    /// <summary>
    /// The pool's collection connector for the collection <paramref name="collectionName"/> of the server nexus (control
    /// protocol §6.11 <c>pool.collection</c>). Nothing is rented until <see cref="IPoolCollectionConnector{T}.GetCollectionAsync"/>.
    /// </summary>
    /// <typeparam name="T">The item type of the collection.</typeparam>
    /// <exception cref="ArgumentException">The server nexus has no collection <paramref name="collectionName"/> of <typeparamref name="T"/>.</exception>
    IPoolCollectionConnector<T> GetCollectionConnector<T>(string collectionName);

    /// <summary>
    /// Reports what the pool still holds: each of its clients that is not disconnected counts as an open client (idle or
    /// rented), and a pool not disposed counts its health check as a timer. A collection connector not released counts
    /// as an open client once it rented one (<see cref="IPoolCollectionConnector{T}"/>). Clean once every rental is
    /// returned, every connector released and the pool disposed.
    /// </summary>
    Task<LeakReport> EndTestAuditAsync();
}

/// <summary>
/// A client rented from an <see cref="IPoolUnderTest"/>. Disposing it returns it to the pool
/// (<see cref="ReturnAsync"/>).
/// </summary>
/// <remarks>
/// <para>
/// The pool owns the connection: <see cref="IClientUnderTest.ConnectAsync()"/>, <see cref="IClientUnderTest.DisconnectAsync"/>,
/// <see cref="IClientUnderTest.CreatePipeAsync"/>, <see cref="IClientUnderTest.List{T}"/>,
/// <see cref="IClientUnderTest.Callbacks{TScript}"/> and <see cref="IClientUnderTest.SignalAsync"/> throw
/// <see cref="NotSupportedException"/>;
/// <see cref="IClientUnderTest.TryConnectAsync()"/> is <see cref="EnsureConnectedAsync"/> (a connect with a cancellation
/// throws <see cref="NotSupportedException"/>); and
/// <see cref="IClientUnderTest.AdvanceTimeAsync"/> moves the clock of the whole pool. The client nexus of pooled clients is
/// not scripted: the server's calls to it complete with their default result.
/// </para>
/// <para>
/// After the return, as with the C# <see cref="IRentedNexusClient{TServerProxy}"/>: <see cref="IClientUnderTest.Proxy{TProxy}"/>
/// and invocations through proxies taken before throw <see cref="ObjectDisposedException"/>, the state is
/// <c>Disconnected</c>, <see cref="IClientUnderTest.WhenDisconnectedAsync"/> completes at
/// once and <see cref="EnsureConnectedAsync"/> returns false. Returning twice does nothing.
/// </para>
/// </remarks>
public interface IRentedClientUnderTest : IClientUnderTest
{
    /// <summary>The pool the client was rented from.</summary>
    IPoolUnderTest Pool { get; }

    /// <summary>
    /// Identifies the pooled client behind this rental: two rentals of the same client have the same id (the pool reused
    /// it). Opaque.
    /// </summary>
    string ClientId { get; }

    /// <summary>True once the rental is returned.</summary>
    bool IsReturned { get; }

    /// <summary>
    /// True when connected; otherwise tries to connect once and returns whether it did. False after the return.
    /// </summary>
    Task<bool> EnsureConnectedAsync();

    /// <summary>
    /// Returns the client to the pool: kept idle when it is connected, disposed otherwise (or when the pool is disposed).
    /// </summary>
    Task ReturnAsync();
}

/// <summary>Creates client pools under test for one language (implemented next to its <see cref="IClientDriverFactory"/>).</summary>
public interface IPoolDriverFactory
{
    /// <summary>
    /// Creates a pool of clients of the nexus pair (<typeparamref name="TClientNexus"/>, <typeparamref name="TServerProxy"/>)
    /// for <paramref name="target"/>; it holds no client yet.
    /// </summary>
    Task<IPoolUnderTest> CreatePoolAsync<TClientNexus, TServerProxy>(ClientTarget target, ClientSpec spec, ClientPoolSpec pool)
        where TClientNexus : ClientNexusBase<TServerProxy>, IInvocationMethodHash, ICollectionConfigurer, new()
        where TServerProxy : ProxyInvocationBase, IInvocationMethodHash, new();
}
