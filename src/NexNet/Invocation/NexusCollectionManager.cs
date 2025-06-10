using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using NexNet.Collections;
using NexNet.Collections.Lists;
using NexNet.Internals;
using NexNet.Pipes;
using NexNet.Transports;

namespace NexNet.Invocation;

internal class NexusCollectionManager : IConfigureCollectionManager
{
    private readonly ConfigBase _config;
    private readonly bool _isServer;
    private Dictionary<ushort, INexusCollection>? _collectionBuilder = new();
    private FrozenDictionary<ushort, INexusCollection>? _collections;

    public NexusCollectionManager(ConfigBase config)
    {
        _config = config;
        _isServer = config is ServerConfig;
    }

    public NexusList<T> GetList<T>(ushort id)
    {
        return (NexusList<T>)_collections![id];
    }
    
    public ValueTask  StartServerCollectionConnection(ushort id, INexusDuplexPipe pipe, INexusSession context)
    {
        var connector = Unsafe.As<INexusCollectionConnector>(_collections![id]);
        return connector.ServerStartCollectionConnection(pipe, context);
    }
    
    public void ConfigureList<T>(ushort id, NexusCollectionMode mode)
    {
        if(_collectionBuilder == null)
            throw new InvalidOperationException("CollectionManager is already configured.  Can't add any new collections.");
        var list = new NexusList<T>(id, mode, _config, _isServer);
        _collectionBuilder.Add(id, list);
        
        // Only broadcast on the server.
        if(_isServer)
            list.StartUpdateBroadcast();
    }

    public void CompleteConfigure()
    {
        if(_collectionBuilder == null)
            throw new InvalidOperationException("CollectionManager is already configured.  Can't re-configure.");
        
        _collections = _collectionBuilder.ToFrozenDictionary();
        _collectionBuilder = null;
    }

    public void SetClientProxySession(ProxyInvocationBase proxy, INexusSession session)
    {
        foreach (var collectionKvp in _collections!)
            Unsafe.As<INexusCollectionConnector>(collectionKvp.Value).TryConfigureProxyCollection(proxy, session);
    }
}
