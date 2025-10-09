using System.Runtime.CompilerServices;
using MemoryPack;

namespace NexNet.Pipes.Channels;

/// <summary>
/// Base message for a single pooled message. Not to be used with unions.
/// </summary>
/// <typeparam name="TMessage">Message type.</typeparam>
public abstract class NexusPooledMessageBase<TMessage> : INexusPooledMessage<TMessage>
    where TMessage : NexusPooledMessageBase<TMessage>, INexusPooledMessage<TMessage>, IMemoryPackable<TMessage>, new()
{
    /// <summary>
    /// Not used as this is not a union.
    /// </summary>
    public static byte UnionId => 0;
    
    /// <summary>
    /// Returns this message to the pool for reuse.
    /// </summary>
    public void Return() => NexusMessagePool<TMessage>.Return(Unsafe.As<TMessage>(this));
}
