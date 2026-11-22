using System.Reflection;
using System.Text.Json.Nodes;
using NexNet.Interop.Reflection;

namespace NexNet.Interop;

/// <summary>
/// The collection connector of a client pool under test (<see cref="IPoolUnderTest.GetCollectionConnector{T}"/>): the
/// runtime's connector (C# <see cref="NexusClientPool{TClientNexus, TServerProxy}.GetCollectionConnector"/>, Java
/// <c>NexusClientPool.collectionConnector</c>, JavaScript <c>NexusClientPool.collectionConnector</c>) for one collection of
/// the server nexus, as a relay server uses it (control protocol §6.11 <c>pool.collection</c>).
/// </summary>
/// <typeparam name="T">The item type of the collection.</typeparam>
/// <remarks>
/// <para>
/// Each <see cref="GetCollectionAsync"/> rents a client from the pool through the runtime's connector. What happens to
/// the rental of an earlier call is the runtime's own: the Java connector returns it, the JavaScript one keeps it until
/// the release, and the C# one stops referencing it. Tests rely only on <see cref="ReleaseAsync"/> giving back the client
/// of the latest call.
/// </para>
/// <para>
/// The rentals of a connector are not counted in <see cref="ClientPoolState.Rented"/> (the rentals of
/// <see cref="IPoolUnderTest.RentAsync"/>). A connector not released, once it rented a client, counts as an open client in
/// the pool's leak audit, and the watches of its collections count as subscriptions.
/// </para>
/// </remarks>
public interface IPoolCollectionConnector<T> : IAsyncDisposable
{
    /// <summary>The pool.</summary>
    IPoolUnderTest Pool { get; }

    /// <summary>The name of the collection (the server nexus property).</summary>
    string CollectionName { get; }

    /// <summary>
    /// Rents a client from the pool through the connector (C# <c>GetCollection</c>, Java <c>collection()</c>, JavaScript
    /// <c>getCollection()</c>) and returns that client's view of the collection, not enabled yet.
    /// </summary>
    /// <exception cref="ClientPoolConnectionException">The new client could not connect.</exception>
    /// <exception cref="ObjectDisposedException">The connector is released or the pool disposed.</exception>
    Task<PooledCollection<T>> GetCollectionAsync();

    /// <summary>
    /// Releases the connector (C# <c>Dispose</c>, Java <c>close()</c>, JavaScript <c>release()</c>): its client goes back
    /// to the pool. The collections it returned must not be used afterwards. Releasing twice does nothing.
    /// </summary>
    Task ReleaseAsync();
}

/// <summary>A collection returned by <see cref="IPoolCollectionConnector{T}.GetCollectionAsync"/>.</summary>
/// <typeparam name="T">The item type of the collection.</typeparam>
/// <param name="list">The rented client's view of the collection.</param>
/// <param name="clientId">The pooled client behind it, as <see cref="IRentedClientUnderTest.ClientId"/>.</param>
public sealed class PooledCollection<T>(IRemoteList<T> list, string clientId)
{
    /// <summary>The rented client's view of the collection.</summary>
    public IRemoteList<T> List { get; } = list ?? throw new ArgumentNullException(nameof(list));

    /// <summary>
    /// Identifies the pooled client the connector rented, as <see cref="IRentedClientUnderTest.ClientId"/> does: the same
    /// id for the same client. Opaque.
    /// </summary>
    public string ClientId { get; } = clientId ?? throw new ArgumentNullException(nameof(clientId));
}

/// <summary>Finds the descriptor of a collection through the generated remote proxy of its server nexus.</summary>
internal static class RemoteCollectionProbe
{
    /// <summary>The descriptor of the collection <paramref name="name"/> of <paramref name="serverType"/> holding <typeparamref name="T"/>.</summary>
    /// <exception cref="ArgumentException">The server nexus has no such collection.</exception>
    public static RemoteCollection Find<T>(Type serverType, string name)
    {
        var probe = new Probe();
        var proxy = NexusTypes.CreateRemoteProxy(NexusTypes.GetRemoteProxyType(serverType), probe);
        var property = proxy.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance, null, typeof(IRemoteList<T>), Type.EmptyTypes, null)
                       ?? throw new ArgumentException($"The server nexus has no collection '{name}' of {typeof(T)}.", nameof(name));
        property.GetValue(proxy);
        return probe.Collection ?? throw new ArgumentException($"'{name}' is not a collection of the server nexus.", nameof(name));
    }

    /// <summary>Records the descriptor the remote proxy asks for; runs nothing.</summary>
    private sealed class Probe : IRemoteInvoker
    {
        public RemoteCollection? Collection { get; private set; }

        public void Invoke(RemoteInvocation invocation) => throw new NotSupportedException();

        public ValueTask InvokeAsync(RemoteInvocation invocation) => throw new NotSupportedException();

        public ValueTask<JsonNode?> InvokeWithResultAsync(RemoteInvocation invocation) => throw new NotSupportedException();

        public IRemoteList<TItem> GetList<TItem>(RemoteCollection collection, TypedCodec<TItem> codec)
        {
            Collection = collection;
            return null!;
        }
    }
}
