using System;
using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using NexNet.Collections;
using NexNet.Collections.Lists;
using NexNet.Internals;
using NexNet.Pipes;

namespace NexNet.Invocation;

internal class NexusCollectionManager : IConfigureCollectionManager
{
    private readonly bool _isServer;
    ImmutableDictionary<ushort, INexusCollection> _lists = ImmutableDictionary<ushort, INexusCollection>.Empty;
    
    private Dictionary<ushort, INexusCollection>? _collectionBuilder = new();
    private FrozenDictionary<ushort, INexusCollection> _collections;

    public NexusCollectionManager(bool isServer)
    {
        _isServer = isServer;
    }

    public NexusList<T> GetList<T>(ushort id)
    {
        return (NexusList<T>)_collections[id];
    }
    
    public ValueTask StartServerCollectionConnection<T>(ushort id, INexusDuplexPipe pipe, INexusSession context)
    {
        var connector = Unsafe.As<INexusCollectionConnector>(_collections[id]);
        return connector.StartServerCollectionConnection(pipe, context);
    }
    
    
    public void AddList<T>(ushort id, NexusCollectionMode mode)
    {
        if(_collectionBuilder == null)
            throw new InvalidOperationException("CollectionManager is already configured.  Can't add any new collections.");
        _collectionBuilder.Add(id, new NexusList<T>(id, mode, _isServer));
    }

    public void CompleteConfigure()
    {
        if(_collectionBuilder == null)
            throw new InvalidOperationException("CollectionManager is already configured.  Can't re-configure.");
        
        _collections = _collectionBuilder.ToFrozenDictionary();
        _collectionBuilder = null;
    }
}

public interface IConfigureCollectionManager
{
    void AddList<T>(ushort id, NexusCollectionMode mode);
    
    void CompleteConfigure();
}
