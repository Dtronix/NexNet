﻿using System;
using System.Buffers;
using System.Runtime.InteropServices;
using MemoryPack;
using NexNet.Cache;

namespace NexNet.Messages;

/// <summary>
/// Base interface for all messages.
/// </summary>
internal interface IMessageBase : IDisposable
{
    /// <summary>
    /// Type of the message.
    /// </summary>
    public static abstract MessageType Type { get; }

    public ICachedMessage? MessageCache { set; }

    ///// <summary>
    ///// Resets the message to its default state for reuse.
    ///// </summary>
    //public void Reset();

    protected static void Return<T>(Memory<T> memory) => Return((ReadOnlyMemory<T>)memory);
    protected static void Return<T>(ReadOnlyMemory<T> memory)
    {
        if (MemoryMarshal.TryGetArray(memory, out var segment) && segment.Array is { Length: > 0 })
        {
            ArrayPool<T>.Shared.Return(segment.Array, false);
        }
    }
}
