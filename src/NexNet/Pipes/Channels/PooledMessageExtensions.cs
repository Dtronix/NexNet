using MemoryPack;

namespace NexNet.Pipes.Channels;

/// <summary>
/// Extensions for pooled messages
/// </summary>
public static class PooledMessageExtensions
{
    /// <summary>
    /// Returns the passed message to the pool.
    /// </summary>
    /// <param name="message">Message to return.</param>
    /// <typeparam name="TMessage">Message type,</typeparam>
    public static void Return<TMessage>(this INexusPooledMessage<TMessage> message)
        where TMessage : class, INexusPooledMessage<TMessage>, IMemoryPackable<TMessage>, new()
    {
        NexusMessagePool<TMessage>.Return((TMessage)message);
    }
    
    public static class NexusMessage<TMessage> 
        where TMessage : class, INexusPooledMessage<TMessage>, IMemoryPackable<TMessage>, new()
    {
        public static TMessage Rent() => NexusMessagePool<TMessage>.Rent();
    }
}
