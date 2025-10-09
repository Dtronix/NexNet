using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using MemoryPack;
using NexNet.Pipes.Channels;

namespace NexNet.Pipes;

/// <summary>
/// Provides extension methods for the <see cref="INexusDuplexPipe"/> interface.
/// These methods allow for the creation of channel readers and writers, both for managed and unmanaged types.
/// </summary>
public static class NexusDuplexPipeExtensions
{
    /// <summary>
    /// Asynchronously creates and returns a <see cref="INexusDuplexChannel{T}"/> for the specified type.
    /// </summary>
    /// <typeparam name="T">The type of data to be transmitted through the <see cref="INexusDuplexPipe"/>.</typeparam>
    /// <param name="pipe">The <see cref="INexusDuplexPipe"/> from which to create the duplex channel.</param>
    /// <returns>A task result which contains the <see cref="INexusDuplexChannel{T}"/>.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static INexusDuplexChannel<T> GetChannel<T>(this INexusDuplexPipe pipe)
    {
        return new NexusDuplexChannel<T>(pipe);
    }
    
    /// <summary>
    /// Asynchronously creates and returns a channel reader for normal and <see cref="MemoryPack.MemoryPackableAttribute"/> types.
    /// </summary>
    /// <typeparam name="T">The type of data to be read from the <see cref="INexusDuplexPipe"/></typeparam>
    /// <param name="pipe"><see cref="INexusDuplexPipe"/> from which to create the channel reader.</param>
    /// <returns>The task containing the channel reader.</returns>
    /// <remarks>
    /// Will await <see cref="INexusDuplexPipe.ReadyTask"/> to ensure the pipe is ready.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static async ValueTask<INexusChannelReader<T>> GetChannelReader<T>(this INexusDuplexPipe pipe)
    {
        await pipe.ReadyTask.ConfigureAwait(false);
        return new NexusChannelReader<T>(pipe.ReaderCore);
    }


    /// <summary>
    /// Asynchronously creates and returns a channel writer for normal and <see cref="MemoryPack.MemoryPackableAttribute"/> types.
    /// </summary>
    /// <typeparam name="T">The type of data to write to the <see cref="INexusDuplexPipe"/></typeparam>
    /// <param name="pipe"><see cref="INexusDuplexPipe"/> from which to create the channel writer.</param>
    /// <returns>The task containing the channel writer.</returns>
    /// <remarks>
    /// Will await <see cref="INexusDuplexPipe.ReadyTask"/> to ensure the pipe is ready.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static async ValueTask<INexusChannelWriter<T>> GetChannelWriter<T>(this INexusDuplexPipe pipe)
    {
        await pipe.ReadyTask.ConfigureAwait(false);
        return new NexusChannelWriter<T>(pipe.WriterCore);
    }
    
    
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static INexusDuplexChannel<TMessage> GetPooledMessageChannel<TMessage>(this INexusDuplexPipe pipe)
        where TMessage : NexusPooledMessageBase<TMessage>, INexusPooledMessage<TMessage>, IMemoryPackable<TMessage>, new()
    {
        return new NexusPooledMessageDuplexChannel<TMessage>(pipe);
    }
    

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static async ValueTask<INexusChannelReader<TMessage>> GetPooledMessageChannelReader<TMessage>(this INexusDuplexPipe pipe)
        where TMessage : NexusPooledMessageBase<TMessage>, INexusPooledMessage<TMessage>, IMemoryPackable<TMessage>, new()
    {
        await pipe.ReadyTask.ConfigureAwait(false);
        return new NexusPooledMessageChannelReaders<TMessage>(pipe.ReaderCore);
    }


    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static async ValueTask<INexusChannelWriter<TMessage>> GetPooledMessageChannelWriter<TMessage>(this INexusDuplexPipe pipe)
        where TMessage : NexusPooledMessageBase<TMessage>, INexusPooledMessage<TMessage>, IMemoryPackable<TMessage>, new()
    {
        await pipe.ReadyTask.ConfigureAwait(false);
        return new NexusPooledMessageChannelWriter<TMessage>(pipe.WriterCore);
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static INexusDuplexChannel<TUnion> GetPooledUnionMessageChannel<TUnion>(this INexusDuplexPipe pipe)
        where TUnion : class, INexusPooledMessageUnion<TUnion>
    {
        return new NexusPooledUnionMessageDuplexChannel<TUnion>(pipe);
    }
    

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static async ValueTask<INexusChannelReader<TUnion>> GetPooledUnionMessageChannelReader<TUnion>(this INexusDuplexPipe pipe)
        where TUnion : class, INexusPooledMessageUnion<TUnion>
    {
        await pipe.ReadyTask.ConfigureAwait(false);
        return new NexusPooledUnionMessageChannelReader<TUnion>(pipe.ReaderCore);
    }


    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static async ValueTask<INexusChannelWriter<TUnion>> GetPooledUnionMessageChannelWriter<TUnion>(this INexusDuplexPipe pipe)
        where TUnion : class, INexusPooledMessageUnion<TUnion>
    {
        await pipe.ReadyTask.ConfigureAwait(false);
        return new NexusPooledUnionMessageChannelWriter<TUnion>(pipe.WriterCore);
    }
}
