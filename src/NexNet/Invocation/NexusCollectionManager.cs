using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using NexNet.Collections;
using NexNet.Collections.Lists;
using NexNet.Diagnostics;
using NexNet.Internals;
using NexNet.Logging;
using NexNet.Pipes;
using NexNet.Pipes.Broadcast;
using NexNet.Transports;

namespace NexNet.Invocation;

internal class NexusCollectionManager : IConfigureCollectionManager
{
    private readonly bool _isServer;
    private Dictionary<ushort, INexusCollection>? _collectionBuilder = new();
    private FrozenDictionary<ushort, INexusCollection>? _collections;
    private readonly INexusLogger? _logger;
    private readonly TimeProvider _time;

    public NexusCollectionManager(INexusLogger? logger, bool isServer, TimeProvider time)
    {
        _logger = logger;
        _isServer = isServer;
        _time = time;
    }

    public INexusList<T> GetList<T>(ushort id)
    {
        var collection = _collections![id];

        // Handle relay type
        if (collection is NexusListRelay<T> relay)
            return relay;

        if (_isServer)
            return (NexusListServer<T>)collection;

        return (NexusListClient<T>)collection;
    }
    
    public ValueTask  StartServerCollectionConnection(ushort id, INexusDuplexPipe pipe, INexusSession context)
    {
        var connector = (INexusBroadcastConnector)_collections![id];
        return connector.ServerStartCollectionConnection(pipe, context);
    }
    
    public void ConfigureList<T>(ushort id, NexusCollectionMode mode)
    {
        if(_collectionBuilder == null)
            throw new InvalidOperationException("CollectionManager is already configured.  Can't add any new collections.");

        INexusCollection list;
        if (_isServer)
        {
            if (mode == NexusCollectionMode.Relay)
            {
                var relay = new NexusListRelay<T>(id, mode, _logger, _time);
                relay.Start();
                list = relay;
            }
            else
            {
                var server = new NexusListServer<T>(id, mode, _logger);
                server.Start();
                list = server;
            }
        }
        else
        {
            list = new NexusListClient<T>(id, mode, _logger);
        }

        _collectionBuilder.Add(id, list);
    }

    public void CompleteConfigure()
    {
        if(_collectionBuilder == null)
            throw new InvalidOperationException("CollectionManager is already configured.  Can't re-configure.");
        
        _collections = _collectionBuilder.ToFrozenDictionary();
        _collectionBuilder.Clear();
        _collectionBuilder = null;
    }

    /// <summary>
    /// The configured collections as seen from <paramref name="session"/>, ordered by ID; empty when none are configured.
    /// </summary>
    public IReadOnlyList<NexusCollectionSnapshot> GetSnapshots(INexusSession session)
    {
        var collections = _collections;
        if (collections == null || collections.Count == 0)
            return [];

        var snapshots = new List<NexusCollectionSnapshot>(collections.Count);
        foreach (var collection in collections)
        {
            if (collection.Value is INexusCollectionSnapshotSource source)
                snapshots.Add(source.GetSnapshot(session));
        }

        snapshots.Sort(static (a, b) => a.Id.CompareTo(b.Id));
        return snapshots;
    }

    public void SetClientProxySession(ProxyInvocationBase proxy, INexusSession session)
    {
        foreach (var collectionKvp in _collections!)
            ((INexusCollectionConnector)collectionKvp.Value).TryConfigureProxyCollection(proxy, session);
    }

    public void Stop()
    {
        if (_collections == null)
            return;

        // Stop relay connections first
        foreach (var collection in _collections!)
        {
            if (collection.Value is INexusListRelay relay)
            {
                relay.StopRelay();
            }
        }

        foreach (var collection in _collections!)
        {
            ((INexusBroadcastConnector)collection.Value).Stop();
        }
    }

    public void Start()
    {
        if (_collections == null)
            return;

        foreach (var collection in _collections!)
        {
            ((INexusBroadcastConnector)collection.Value).Start();
        }

        // Start relay connections
        foreach (var nexusCollection in _collections)
        {
            try
            {
                if (nexusCollection.Value is INexusListRelay relay)
                {
                    relay.StartRelay();
                }
            }
            catch (Exception e)
            {
                _logger?.LogError(e, $"Could not start relay for nexus collection with ID {nexusCollection.Key}");
            }
        }
    }
}
