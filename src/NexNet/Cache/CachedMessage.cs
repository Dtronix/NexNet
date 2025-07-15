using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using MemoryPack;
using NexNet.Messages;

namespace NexNet.Cache;

internal class CachedCachedMessage<T> : ICachedMessage
    where T : class, IMessageBase, new()
{
    /// <summary>
    /// Thread-local cache of message items.
    /// </summary>
    [ThreadStatic]
    private static Stack<T>? _cache;

    private static readonly ConcurrentBag<Stack<T>> _caches = new();
    
    public T Rent()
    {
        InitStack();
        T? cachedItem = null;
        while (cachedItem == null)
        {
            if (!_cache!.TryPop(out cachedItem))
                cachedItem = new T();
        }

        cachedItem.MessageCache = this;

        return cachedItem;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public T? Deserialize(in ReadOnlySequence<byte> bodySequence)
    {
        InitStack();
        T? cachedItem = null;
        while (cachedItem == null)
        {
            if (!_cache!.TryPop(out cachedItem))
                cachedItem = new T();
        }

        cachedItem.MessageCache = this;

        MemoryPackSerializer.Deserialize(bodySequence, ref cachedItem);

        return cachedItem;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public IMessageBase DeserializeInterface(in ReadOnlySequence<byte> bodySequence)
    {
        InitStack();
        
        T? cachedItem = null;
        while (cachedItem == null)
        {
            if (!_cache!.TryPop(out cachedItem))
                cachedItem = new T();
        }

        cachedItem.MessageCache = this;

        MemoryPackSerializer.Deserialize(bodySequence, ref cachedItem);

        return cachedItem!;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Return(IMessageBase? item)
    {
        if (item == null)
            return;
        
        InitStack();
        _cache!.Push(Unsafe.As<T>(item));
    }

    public void Clear()
    {
        foreach (var cache in _caches)
        {
            cache.Clear();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void InitStack()
    {
        if (_cache == null)
        {
            _cache = new Stack<T>();

            // Add the cache to the list of caches so that it can be cleared later.
            _caches.Add(_cache);
        }
    }
}
