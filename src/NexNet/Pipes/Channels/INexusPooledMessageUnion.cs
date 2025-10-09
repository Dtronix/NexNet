namespace NexNet.Pipes.Channels;

/// <summary>
/// Marker interface for union groups that automatically registers message types
/// </summary>
public interface INexusPooledMessageUnion<out TUnion>
    where TUnion : class, INexusPooledMessageUnion<TUnion>
{
    /// <summary>Register all message types for this union</summary>
    static abstract void RegisterMessages(INexusUnionBuilder<TUnion> registerer);
}
