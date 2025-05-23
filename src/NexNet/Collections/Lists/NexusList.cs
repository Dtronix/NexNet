using System.Collections;
using System.Collections.Generic;
using NexNet.Internals.Collections.Lists;
using NexNet.Invocation;
using NexNet.Pipes;
using NexNetSample.Asp.Shared;

namespace NexNet.Collections;

public class NexusList<T>
{
    private readonly NexusDictionaryMode _mode;
    private readonly INexusDuplexPipe _duplexPipe;
    private VersionedList<T> _list = new();
    private readonly NexusDuplexChannel<INexusListOperation> _channel;

    internal NexusList(NexusDictionaryMode mode, INexusDuplexPipe duplexPipe)
    {
        _mode = mode;
        _duplexPipe = duplexPipe;
        _channel = new NexusDuplexChannel<INexusListOperation>(duplexPipe);
        

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
