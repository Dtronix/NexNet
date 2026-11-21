using System.Collections.Concurrent;
using Microsoft.Extensions.Time.Testing;
using NexNet.Collections;
using NexNet.Interop.Reflection;
using NexNet.Invocation;
using NexNet.Messages;
using NexNet.Transports;

namespace NexNet.Interop.InProc;

/// <summary>
/// The C# client pool in process, as an <see cref="IPoolUnderTest"/>: a real <see cref="NexusClientPool{TClientNexus, TServerProxy}"/>
/// whose clients use the configuration of the <see cref="ClientSpec"/>, rented through the same typed-JSON proxies as the
/// language hosts.
/// </summary>
/// <typeparam name="TClientNexus">The client nexus.</typeparam>
/// <typeparam name="TServerProxy">Its server proxy.</typeparam>
public sealed class InProcPoolDriver<TClientNexus, TServerProxy> : IPoolUnderTest
    where TClientNexus : ClientNexusBase<TServerProxy>, IInvocationMethodHash, ICollectionConfigurer, new()
    where TServerProxy : ProxyInvocationBase, IInvocationMethodHash, new()
{
    private readonly ClientConfig _config;
    private readonly FakeTimeProvider? _fakeTime;
    private readonly object _gate = new();

    // Pooled clients by the identity of their server proxy: the pool hands out the same proxy for the same client.
    private readonly Dictionary<object, PooledRecord> _clients = new(ReferenceEqualityComparer.Instance);
    private readonly List<Rental> _rentals = new();
    private int _nextId;
    private int _disposed;

    /// <summary>Creates the pool; it holds no client yet.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A member of <paramref name="pool"/> is out of range.</exception>
    public InProcPoolDriver(ClientTarget target, ClientSpec spec, ClientPoolSpec pool)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(spec);
        if (spec.OnInvocationException != null)
            throw new NotSupportedException("The client nexus of pooled clients is not scripted: ClientSpec.OnInvocationException needs a client.");
        ArgumentNullException.ThrowIfNull(pool);
        pool.Validate();
        Target = target;
        Capabilities = ClientLanguages.Get(ClientLanguages.CSharp).Capabilities;
        _config = InProcClientConfig.Create(spec, spec.Transport ?? target.Transport, out _fakeTime);
        var poolConfig = new NexusClientPoolConfig(_config)
        {
            MaxConnections = pool.MaxConnections ?? ClientPoolSpec.DefaultMaxConnections,
            MaxIdleTime = TimeSpan.FromMilliseconds(pool.MaxIdleTimeMs ?? ClientPoolSpec.DefaultMaxIdleTimeMs),
            MinIdleConnections = pool.MinIdleConnections ?? ClientPoolSpec.DefaultMinIdleConnections,
        };
        Pool = new NexusClientPool<TClientNexus, TServerProxy>(poolConfig);
    }

    /// <summary>The C# pool. In-process only: tests must not depend on it to stay portable.</summary>
    public NexusClientPool<TClientNexus, TServerProxy> Pool { get; }

    /// <inheritdoc />
    public ClientTarget Target { get; }

    /// <inheritdoc />
    public ClientCapabilities Capabilities { get; }

    /// <inheritdoc />
    public async Task<IRentedClientUnderTest> RentAsync(CancellationToken cancellationToken = default)
    {
        var rented = await Pool.RentClientAsync(cancellationToken).ConfigureAwait(false);
        PooledRecord record;
        lock (_gate)
        {
            var proxy = rented.Proxy;
            if (!_clients.TryGetValue(proxy, out record!))
            {
                record = new PooledRecord("pc" + (++_nextId).ToString(System.Globalization.CultureInfo.InvariantCulture));
                _clients[proxy] = record;
            }

            record.Disconnected = rented.DisconnectedTask;
        }

        var inner = new InProcPooledClient<TServerProxy>(Target, Capabilities, rented);
        var rental = new Rental(record, inner);
        lock (_gate)
            _rentals.Add(rental);

        return new RentedClient(
            inner,
            this,
            record.Id,
            async () =>
            {
                var connected = await rented.EnsureConnectedAsync().ConfigureAwait(false);
                record.Disconnected = rented.DisconnectedTask;
                return connected;
            },
            () =>
            {
                lock (_gate)
                    _rentals.Remove(rental);
                rented.Dispose();
                return Task.CompletedTask;
            });
    }

    /// <inheritdoc />
    public Task<ClientPoolState> GetStateAsync()
    {
        int rented;
        lock (_gate)
            rented = _rentals.Count;
        return Task.FromResult(new ClientPoolState(Pool.MaxConnections, Pool.AvailableConnections, rented));
    }

    /// <inheritdoc />
    public Task AdvanceTimeAsync(TimeSpan by)
    {
        if (_fakeTime == null)
            throw new InvalidOperationException($"{Target}: create the pool with ClientSpec.FakeTime to move its clock.");
        _fakeTime.Advance(by);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<LeakReport> EndTestAuditAsync()
    {
        // Give invocations in flight and clients being disposed (the pool disposes without waiting) a moment to settle.
        var deadline = DateTime.UtcNow.AddSeconds(1);
        while (DateTime.UtcNow < deadline && (Pending() > 0 || Open().Length > 0))
            await Task.Delay(10).ConfigureAwait(false);

        var details = new List<string>();
        Rental[] rentals;
        lock (_gate)
            rentals = _rentals.ToArray();

        var open = Open();
        foreach (var record in open)
        {
            details.Add(rentals.Any(r => r.Record == record)
                ? $"pooled client {record.Id} is connected and rented, not returned"
                : $"pooled client {record.Id} is connected, idle in a pool not disposed");
        }

        var timers = Volatile.Read(ref _disposed) == 0 ? 1 : 0;
        if (timers > 0)
            details.Add($"the health check of the pool {Target} runs: the pool is not disposed");

        var pending = Pending();
        if (pending > 0)
            details.Add($"{pending} invocation(s) of rented clients still running");

        return new LeakReport(open.Length, 0, timers, pending, 0, details);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        // Nothing waits for the disconnect message at the end of a test.
        _config.DisconnectDelay = 0;
        await Pool.DisposeAsync().ConfigureAwait(false);
    }

    private int Pending()
    {
        lock (_gate)
            return _rentals.Sum(r => r.Client.Pending);
    }

    private PooledRecord[] Open()
    {
        lock (_gate)
            return _clients.Values.Where(c => !c.Disconnected.IsCompleted).OrderBy(c => c.Id, StringComparer.Ordinal).ToArray();
    }

    private sealed class PooledRecord(string id)
    {
        public string Id { get; } = id;

        /// <summary>The disconnection of the client's latest session, as of its latest rent or ensure-connected.</summary>
        public volatile Task Disconnected = Task.CompletedTask;
    }

    private sealed record Rental(PooledRecord Record, InProcPooledClient<TServerProxy> Client);
}

/// <summary>A C# client rented from a <see cref="NexusClientPool{TClientNexus, TServerProxy}"/>, behind a <see cref="RentedClient"/>.</summary>
internal sealed class InProcPooledClient<TServerProxy> : IClientUnderTest
    where TServerProxy : ProxyInvocationBase, IInvocationMethodHash, new()
{
    private readonly IRentedNexusClient<TServerProxy> _rented;
    private readonly InProcInvoker _invoker;
    private readonly ConcurrentDictionary<Type, object> _proxies = new();

    public InProcPooledClient(ClientTarget target, ClientCapabilities capabilities, IRentedNexusClient<TServerProxy> rented)
    {
        Target = target;
        Capabilities = capabilities;
        _rented = rented;

        // The rental's proxy throws ObjectDisposedException once it is returned, so do calls through earlier proxies.
        _invoker = new InProcInvoker(() => _rented.Proxy);
    }

    public ClientTarget Target { get; }

    public ClientCapabilities Capabilities { get; }

    /// <summary>Invocations started and not completed.</summary>
    public int Pending => _invoker.Pending;

    public Task ConnectAsync() => throw new NotSupportedException();

    public Task<bool> TryConnectAsync() => _rented.EnsureConnectedAsync();

    public Task DisconnectAsync() => throw new NotSupportedException();

    public Task<ConnectionState> GetStateAsync() => Task.FromResult(_rented.State);

    public Task<byte[]?> GetRemoteCertificateAsync() => throw new NotSupportedException();

    public Task<bool> GetIsSecureAsync() => throw new NotSupportedException();

    public Task<RemoteEndpoint> GetRemoteEndpointAsync() => throw new NotSupportedException();

    public async Task WaitForStateAsync(ConnectionState state, double seconds)
    {
        // The rental raises no state events: poll.
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (_rented.State != state)
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException($"{Target}: state {state} not reached within {seconds} s; the state is {_rented.State}.");
            await Task.Delay(10).ConfigureAwait(false);
        }
    }

    public async Task<DisconnectReason> WhenDisconnectedAsync(double seconds)
    {
        var task = _rented.DisconnectedTask;
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(seconds)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException($"{Target}: not disconnected within {seconds} s.");
        }

        return task is Task<DisconnectReason> withReason ? withReason.Result : DisconnectReason.None;
    }

    public Task WhenReadyAsync(double seconds) => WaitForStateAsync(ConnectionState.Connected, seconds);

    public TProxy Proxy<TProxy>()
        => (TProxy)_proxies.GetOrAdd(typeof(TProxy), t => NexusTypes.CreateRemoteProxy(NexusTypes.GetRemoteProxyType(t), _invoker));

    public TScript Callbacks<TScript>() => throw new NotSupportedException();

    public Task<IRemotePipe> CreatePipeAsync() => throw new NotSupportedException();

    public IRemoteList<T> List<T>(string collectionName) => throw new NotSupportedException();

    public Task SignalAsync(string name) => throw new NotSupportedException();

    public Task BarrierAsync() => _invoker.WhenDetachedCompleteAsync();

    public Task AdvanceTimeAsync(TimeSpan by) => throw new NotSupportedException();

    public Task<LeakReport> EndTestAuditAsync()
    {
        var pending = _invoker.Pending;
        return Task.FromResult(pending == 0
            ? LeakReport.Clean
            : new LeakReport(0, 0, 0, pending, 0, [$"{pending} invocation(s) still running"]));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
