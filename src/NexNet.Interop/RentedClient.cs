using NexNet.Messages;

namespace NexNet.Interop;

/// <summary>
/// The <see cref="IRentedClientUnderTest"/> of every driver: the rules of a rental (what the pool owns, what holds after
/// the return) on top of the driver's view of the pooled client.
/// </summary>
internal sealed class RentedClient : IRentedClientUnderTest
{
    private const string OwnedByPool = "A rented client's connection belongs to its pool; use EnsureConnectedAsync, or return it.";

    private readonly IClientUnderTest _inner;
    private readonly Func<Task<bool>> _ensureConnected;
    private readonly Func<Task> _return;
    private int _returned;

    /// <param name="inner">The driver's view of the pooled client.</param>
    /// <param name="pool">The pool.</param>
    /// <param name="clientId">The pooled client's id.</param>
    /// <param name="ensureConnected">Ensures the pooled client is connected.</param>
    /// <param name="returnToPool">Returns the pooled client; called once.</param>
    public RentedClient(IClientUnderTest inner, IPoolUnderTest pool, string clientId, Func<Task<bool>> ensureConnected, Func<Task> returnToPool)
    {
        _inner = inner;
        Pool = pool;
        ClientId = clientId;
        _ensureConnected = ensureConnected;
        _return = returnToPool;
    }

    /// <inheritdoc />
    public IPoolUnderTest Pool { get; }

    /// <inheritdoc />
    public string ClientId { get; }

    /// <inheritdoc />
    public bool IsReturned => Volatile.Read(ref _returned) != 0;

    /// <inheritdoc />
    public ClientTarget Target => _inner.Target;

    /// <inheritdoc />
    public ClientCapabilities Capabilities => _inner.Capabilities;

    /// <inheritdoc />
    public Task ConnectAsync() => throw new NotSupportedException(OwnedByPool);

    /// <inheritdoc />
    public Task<bool> TryConnectAsync() => EnsureConnectedAsync();

    /// <inheritdoc />
    public Task DisconnectAsync() => throw new NotSupportedException(OwnedByPool);

    /// <inheritdoc />
    public Task<ConnectionState> GetStateAsync()
        => IsReturned ? Task.FromResult(ConnectionState.Disconnected) : _inner.GetStateAsync();

    /// <inheritdoc />
    public Task WaitForStateAsync(ConnectionState state, double seconds)
    {
        if (!IsReturned)
            return _inner.WaitForStateAsync(state, seconds);
        return state == ConnectionState.Disconnected
            ? Task.CompletedTask
            : Task.FromException(new TimeoutException($"{Target}: the rental is returned; its state stays Disconnected, not {state}."));
    }

    /// <inheritdoc />
    public Task<DisconnectReason> WhenDisconnectedAsync(double seconds)
        => IsReturned ? Task.FromResult(DisconnectReason.None) : _inner.WhenDisconnectedAsync(seconds);

    /// <inheritdoc />
    public Task WhenReadyAsync(double seconds)
    {
        ThrowIfReturned();
        return _inner.WhenReadyAsync(seconds);
    }

    /// <inheritdoc />
    public TProxy Proxy<TProxy>()
    {
        ThrowIfReturned();
        return _inner.Proxy<TProxy>();
    }

    /// <inheritdoc />
    public TScript Callbacks<TScript>()
        => throw new NotSupportedException("The client nexus of pooled clients is not scripted; the server's calls to it complete with their default result.");

    /// <inheritdoc />
    public Task<IRemotePipe> CreatePipeAsync()
        => throw new NotSupportedException("Rented clients create no pipes; use a client of Harness.CreateClientAsync.");

    /// <inheritdoc />
    public IRemoteList<T> List<T>(string collectionName)
    {
        ThrowIfReturned();
        return _inner.List<T>(collectionName);
    }

    /// <inheritdoc />
    public Task SignalAsync(string name)
        => throw new NotSupportedException("The client nexus of pooled clients is not scripted, so it waits for no signal.");

    /// <inheritdoc />
    public Task BarrierAsync() => _inner.BarrierAsync();

    /// <inheritdoc />
    public Task AdvanceTimeAsync(TimeSpan by) => Pool.AdvanceTimeAsync(by);

    /// <inheritdoc />
    public Task<LeakReport> EndTestAuditAsync() => IsReturned ? Task.FromResult(LeakReport.Clean) : _inner.EndTestAuditAsync();

    /// <inheritdoc />
    public Task<bool> EnsureConnectedAsync() => IsReturned ? Task.FromResult(false) : _ensureConnected();

    /// <inheritdoc />
    public Task ReturnAsync() => Interlocked.Exchange(ref _returned, 1) != 0 ? Task.CompletedTask : _return();

    /// <inheritdoc />
    public async ValueTask DisposeAsync() => await ReturnAsync().ConfigureAwait(false);

    private void ThrowIfReturned()
    {
        if (IsReturned)
            throw new ObjectDisposedException(nameof(IRentedClientUnderTest), $"{Target}: the rental of {ClientId} is returned.");
    }
}
