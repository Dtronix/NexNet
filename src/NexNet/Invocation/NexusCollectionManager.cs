using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using NexNet.Collections;
using NexNet.Collections.Lists;
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

    public NexusCollectionManager(INexusLogger? logger, bool isServer)
    {
        _logger = logger;
        _isServer = isServer;
    }

    public INexusList<T> GetList<T>(ushort id)
    {
        if(_isServer)
         return (NexusListServer<T>)_collections![id];
        
        return (NexusListClient<T>)_collections![id];
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
            var server = new NexusListServer<T>(id, mode, _logger);
            server.Start();
            list = server;
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

    public void SetClientProxySession(ProxyInvocationBase proxy, INexusSession session)
    {
        foreach (var collectionKvp in _collections!)
            ((INexusCollectionConnector)collectionKvp.Value).TryConfigureProxyCollection(proxy, session);
    }

    public void Stop()
    {
        if (_collections == null)
            return;

        foreach (var collection in _collections!)
        {
            ((INexusBroadcastConnector)collection.Value).Stop();
        }

        //foreach (var nexusCollection in _collections)
        //{
        //    ((NexusCollection)nexusCollection.Value).StopRelay();
        //}
    }

    public void Start()
    {
        if (_collections == null)
            return;
        
        foreach (var collection in _collections!)
        {
            ((INexusBroadcastConnector)collection.Value).Start();
        }

        
        foreach (var nexusCollection in _collections)
        {
            //try
            //{
            //    ((NexusCollection)nexusCollection.Value).StartRelay();
            //}
            //catch (Exception e)
            //{
            //    _logger?.LogError(e, $"Could not start relay for nexus collection with ID {nexusCollection.Key}");
            //}

        }
    }
}
