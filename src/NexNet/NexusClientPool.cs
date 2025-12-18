using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NexNet.Collections;
using NexNet.Internals.Collections;
using NexNet.Invocation;
using NexNet.Logging;
using NexNet.Transports;

namespace NexNet;

/// <summary>
/// Manages a pool of NexusClient connections that can be rented and returned, similar to GRPC channels.
/// Automatically handles connection health, reconnection, and resource pooling.
/// </summary>
/// <typeparam name="TClientNexus">Nexus used by clients for incoming invocation handling.</typeparam>
/// <typeparam name="TServerProxy">Server proxy implementation used for all remote invocations.</typeparam>
public sealed class NexusClientPool<TClientNexus, TServerProxy> : IAsyncDisposable
    where TClientNexus : ClientNexusBase<TServerProxy>, IMethodInvoker, IInvocationMethodHash, ICollectionConfigurer, new()
    where TServerProxy : ProxyInvocationBase, IProxyInvoker, IInvocationMethodHash, new()
{
    private readonly NexusClientPoolConfig _config;
    private readonly Func<TClientNexus> _nexusFactory;
    private readonly ConcurrentRemovableQueue<PooledClient> _availableClients;
    private readonly SemaphoreSlim _semaphore;
    private readonly Timer _healthCheckTimer;
    private volatile bool _disposed;

    /// <summary>
    /// Maximum number of connections in the pool.
    /// </summary>
    public int MaxConnections => _config.MaxConnections;

    /// <summary>
    /// Number of available connections in the pool.
    /// </summary>
    public int AvailableConnections => _availableClients.Count;

    /// <summary>
    /// Creates a new client pool with the specified configuration.
    /// </summary>
    /// <param name="config">Pool configuration including client config, max connections, and idle timeout.</param>
    /// <param name="nexusFactory">Factory function to create nexus instances. If null, uses default constructor.</param>
    public NexusClientPool(NexusClientPoolConfig config, Func<TClientNexus>? nexusFactory = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(config.MaxConnections, 0);
        ArgumentOutOfRangeException.ThrowIfNegative(config.MinIdleConnections);

        _config = config;
        _nexusFactory = nexusFactory ?? (() => new TClientNexus());
        _availableClients = new ConcurrentRemovableQueue<PooledClient>();
        _semaphore = new SemaphoreSlim(config.MaxConnections, config.MaxConnections);
        
        // Set health check interval to be more frequent than idle timeout, but with reasonable bounds
        var healthCheckInterval = TimeSpan.FromMilliseconds(Math.Max(100, Math.Min(30000, config.MaxIdleTime.TotalMilliseconds / 4)));
        _healthCheckTimer = new Timer(PerformHealthAndIdleCheck, null, healthCheckInterval, healthCheckInterval);
    }

    /// <summary>
    /// Gets a collection connector that provides access to a Nexus collection through pooled client connections.
    /// </summary>
    /// <param name="collectionSelector">Function that selects the collection from the server proxy.</param>
    /// <returns>A collection connector that manages access to the specified collection.</returns>
    public INexusCollectionClientConnector GetCollectionConnector(Func<TServerProxy, INexusCollection> collectionSelector)
    {
        return new NexusCollectionClientConnector(this, collectionSelector);
    }


    /// <summary>
    /// Rents a connected client from the pool. The client must be returned via disposal of the returned wrapper.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for the rent operation.</param>
    /// <returns>A rented client wrapper that automatically returns the client when disposed.</returns>
    /// <exception cref="ObjectDisposedException">Thrown if the pool has been disposed.</exception>
    public async Task<IRentedNexusClient<TServerProxy>> RentClientAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);

        // Double-check after acquiring semaphore to handle TOCTOU race
        if (_disposed)
        {
            _semaphore.Release();
            throw new ObjectDisposedException(nameof(NexusClientPool<TClientNexus, TServerProxy>));
        }

        try
        {
            PooledClient? pooledClient = null;

            // Try to get an existing healthy client
            while (_availableClients.TryDequeue(out var candidate))
            {
                if (candidate.IsHealthy)
                {
                    // Update last used time when renting
                    candidate.UpdateLastUsed();
                    pooledClient = candidate;
                    break;
                }

                // Client is unhealthy, dispose it
                _ = candidate.DisposeAsync();
            }

            // Create a new client if we don't have a healthy one
            if (pooledClient == null)
            {
                var nexus = _nexusFactory();
                var client = new NexusClient<TClientNexus, TServerProxy>(_config.ClientConfig, nexus);
                pooledClient = new PooledClient(client);

                // Attempt to connect
                var result = await client.TryConnectAsync(cancellationToken).ConfigureAwait(false);
                if (!result.Success)
                {
                    await pooledClient.DisposeAsync().ConfigureAwait(false);
                    _config.ClientConfig.Logger?.LogError(result.Exception, "Failed to connect after renting.");
                    throw new ClientPoolConnectionException("Failed to connect after renting.", result);
                }
            }

            return new RentedClientWrapper(this, pooledClient);
        }
        catch
        {
            _semaphore.Release();
            throw;
        }
    }

    /// <summary>
    /// Returns a client to the pool for reuse.
    /// </summary>
    internal void ReturnClient(PooledClient client)
    {
        if (_disposed)
        {
            _ = client.DisposeAsync();
        }
        else if (client.IsHealthy)
        {
            // Update last used time when returning
            client.UpdateLastUsed();
            _availableClients.Enqueue(client);
        }
        else
        {
            _ = client.DisposeAsync();
        }

        _semaphore.Release();
    }

    private void PerformHealthAndIdleCheck(object? state)
    {
        if (_disposed)
            return;

        var now = DateTime.UtcNow;
        var minRequired = _config.MinIdleConnections;
        var processedClients = new List<PooledClient>();
        var healthyCount = 0;

        // Count how many clients we're processing to determine idle eviction eligibility
        var currentQueueSize = _availableClients.Count;
        
        // Process only the clients currently in the queue to avoid infinite processing
        // of newly returned clients during health check
        foreach (var client in _availableClients)
        {
            if (!client.IsHealthy)
            {
                // Client is unhealthy, dispose it
                _availableClients.Remove(client);
                _ = client.DisposeAsync();
                continue;
            }

            // Client is healthy - check if it should be kept or evicted for idle time
            var idleTime = now - client.LastUsed;
            var shouldKeepForMinConnections = healthyCount < minRequired;
            
            if (idleTime < _config.MaxIdleTime || shouldKeepForMinConnections)
            {
                // Keep this client
                healthyCount++;
            }
            else
            {
                // Client has been idle too long and we have enough connections
                _availableClients.Remove(client);
                _ = client.DisposeAsync();
            }
        }

        // Put the healthy, non-idle clients back into the queue
        foreach (var client in processedClients)
        {
            _availableClients.Enqueue(client);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;

        await _healthCheckTimer.DisposeAsync().ConfigureAwait(false);

        // Dispose all available clients
        while (_availableClients.TryDequeue(out var client))
        {
            await client.DisposeAsync().ConfigureAwait(false);
        }

        _semaphore.Dispose();
    }

    /// <summary>
    /// Wrapper for pooled clients that tracks health, lifetime, and idle time.
    /// </summary>
    internal sealed class PooledClient : IAsyncDisposable
    {
        private readonly NexusClient<TClientNexus, TServerProxy> _client;
        private volatile bool _disposed;
        private DateTime _lastUsed;

        public PooledClient(NexusClient<TClientNexus, TServerProxy> client)
        {
            _client = client;
            _lastUsed = DateTime.UtcNow;
        }

        public NexusClient<TClientNexus, TServerProxy> Client => _client;

        public bool IsHealthy => !_disposed && _client.State == ConnectionState.Connected;

        public DateTime LastUsed 
        { 
            get => _lastUsed;
            private set => _lastUsed = value;
        }

        public void UpdateLastUsed()
        {
            _lastUsed = DateTime.UtcNow;
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
                return;

            _disposed = true;
            await _client.DisposeAsync().ConfigureAwait(false);
        }
    }

    private sealed class NexusCollectionClientConnector : INexusCollectionClientConnector
    {
        private readonly NexusClientPool<TClientNexus, TServerProxy> _pool;
        private readonly Func<TServerProxy, INexusCollection> _collectionSelector;
        private IRentedNexusClient<TServerProxy>? _clientRental;
 
        public NexusCollectionClientConnector(
            NexusClientPool<TClientNexus, TServerProxy> pool,
            Func<TServerProxy, INexusCollection> collectionSelector)
        {
            _pool = pool;
            _collectionSelector = collectionSelector;
        }



        public async ValueTask<INexusCollection> GetCollection()
        {
            _clientRental = await _pool.RentClientAsync().ConfigureAwait(false);
            return _collectionSelector.Invoke(_clientRental.Proxy);
        }
        
        public void Dispose()
        {
            _clientRental?.Dispose();
        }
    }

    /// <summary>
    /// Wrapper that implements IRentedNexusClient and automatically returns clients to the pool.
    /// </summary>
    private sealed class RentedClientWrapper : IRentedNexusClient<TServerProxy>
    {
        private readonly NexusClientPool<TClientNexus, TServerProxy> _pool;
        private PooledClient? _pooledClient;

        public RentedClientWrapper(NexusClientPool<TClientNexus, TServerProxy> pool, PooledClient pooledClient)
        {
            _pool = pool;
            _pooledClient = pooledClient;
        }

        public TServerProxy Proxy => _pooledClient?.Client.Proxy ?? throw new ObjectDisposedException(nameof(RentedClientWrapper));

        public ConnectionState State => _pooledClient?.Client.State ?? ConnectionState.Disconnected;

        public Task DisconnectedTask => _pooledClient?.Client.DisconnectedTask ?? Task.CompletedTask;

        public async Task<bool> EnsureConnectedAsync(CancellationToken cancellationToken = default)
        {
            if (_pooledClient == null)
                return false;

            if (_pooledClient.Client.State == ConnectionState.Connected)
                return true;

            var result = await _pooledClient.Client.TryConnectAsync(cancellationToken).ConfigureAwait(false);
            return result.Success;
        }

        public void Dispose()
        {
            var client = Interlocked.Exchange(ref _pooledClient, null);
            if (client != null)
            {
                _pool.ReturnClient(client);
            }
        }
    }
}
