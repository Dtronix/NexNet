using MemoryPack;

namespace NexNet.Pipes.Channels;

/// <summary>
/// Builder for unions.
/// </summary>
/// <typeparam name="TUnion">Union type</typeparam>
public interface INexusPooledMessageUnionBuilder<in TUnion> 
    where TUnion : class, INexusPooledMessageUnion<TUnion>
{
    /// <summary>
    /// Adds the provided message which is contained by this union.
    /// </summary>
    /// <typeparam name="TMessage"></typeparam>
    public void Add<TMessage>()
        where TMessage : class, TUnion, INexusPooledMessage<TMessage>, IMemoryPackable<TMessage>, new();
}
