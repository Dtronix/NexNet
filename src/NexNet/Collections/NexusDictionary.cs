using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using NexNet.Invocation;

namespace NexNet.Collections;
public class NexusDictionary<TKey, TValue> : IDictionary<TKey, TValue> 
    where TKey : notnull
{
    private readonly NexusCollectionMode _mode;
    private readonly Dictionary<TKey, TValue> _inner = new();
    
    public int Count => _inner.Count;
    public bool IsReadOnly { get; } 
    
    internal NexusDictionary(NexusCollectionMode mode, IProxyInvoker invoker, int id)
    {
        _mode = mode;
        //invoker.ProxyGetDuplexPipeInitialId()
    }
    public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator()
    {
        throw new System.NotImplementedException();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }

    public void Add(KeyValuePair<TKey, TValue> item)
    {
        throw new System.NotImplementedException();
    }

    public void Clear()
    {
        throw new System.NotImplementedException();
    }

    public bool Contains(KeyValuePair<TKey, TValue> item)
    {
        throw new System.NotImplementedException();
    }

    public void CopyTo(KeyValuePair<TKey, TValue>[] array, int arrayIndex)
    {
        throw new System.NotImplementedException();
    }

    public bool Remove(KeyValuePair<TKey, TValue> item)
    {
        throw new System.NotImplementedException();
    }


    public void Add(TKey key, TValue value)
    {
        throw new System.NotImplementedException();
    }

    public bool ContainsKey(TKey key)
    {
        throw new System.NotImplementedException();
    }

    public bool Remove(TKey key)
    {
        throw new System.NotImplementedException();
    }

    public bool TryGetValue(TKey key, [MaybeNullWhen(false)] out TValue value)
    {
        throw new System.NotImplementedException();
    }

    public TValue this[TKey key]
    {
        get => throw new System.NotImplementedException();
        set => throw new System.NotImplementedException();
    }

    public ICollection<TKey> Keys { get; }
    public ICollection<TValue> Values { get; }
}
