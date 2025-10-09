using System.Runtime.CompilerServices;
using MemoryPack;

namespace NexNet.Pipes.Channels;

/// <summary>
/// Base interface for all union messages with pooling support.
/// </summary>
public interface INexusPooledMessage<TMessage> 
    where TMessage : class, INexusPooledMessage<TMessage>, IMemoryPackable<TMessage>, new()
{
    /// <summary>Unique identifier for this message type within its union</summary>
    static abstract byte UnionId { get; }
    
    /// <summary>
    /// Returns this message to the pool.
    /// </summary>
    public void Return() => NexusMessagePool<TMessage>.Return(Unsafe.As<TMessage>(this));
}
