using System.Collections;
using System.Collections.Generic;
using NexNet.Internals.Collections.Lists;
using NexNet.Invocation;

namespace NexNet.Collections;

public class NexusList<T> : IList<T> 
{
    private readonly NexusDictionaryMode _mode;
    private VersionedList<T> _list = new();
    
    internal NexusList(NexusDictionaryMode mode, IProxyInvoker invoker, int id)
    {
        _mode = mode;
        //invoker.ProxyGetDuplexPipeInitialId()
    }

    /// <inheritdoc />
    public IEnumerator<T> GetEnumerator()
    {
        return _list.GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }

    public void Add(T item)
    {
        _list.GetEnumerator();
    }

    public void Clear()
    {
        throw new System.NotImplementedException();
    }

    public bool Contains(T item)
    {
        throw new System.NotImplementedException();
    }

    public void CopyTo(T[] array, int arrayIndex)
    {
        throw new System.NotImplementedException();
    }

    public bool Remove(T item)
    {
        throw new System.NotImplementedException();
    }

    public int Count { get; }
    public bool IsReadOnly { get; }
    public int IndexOf(T item)
    {
        throw new System.NotImplementedException();
    }

    public void Insert(int index, T item)
    {
        throw new System.NotImplementedException();
    }

    public void RemoveAt(int index)
    {
        throw new System.NotImplementedException();
    }

    public T this[int index]
    {
        get => throw new System.NotImplementedException();
        set => throw new System.NotImplementedException();
    }
}
