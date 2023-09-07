using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace NexNet.Pipes;

/// <summary>
/// Provides extension methods for the <see cref="INexusDuplexPipe"/> interface.
/// These methods allow for the creation of channel readers and writers, both for managed and unmanaged types.
/// </summary>
public static class NexusDuplexPipeExtensions
{
    /// <summary>
    /// Asynchronously creates and returns an unmanaged duplex channel for the specified type.
    /// </summary>
    /// <typeparam name="T">The type of unmanaged data to be transmitted through the <see cref="INexusDuplexPipe"/>.</typeparam>
    /// <param name="pipe">The <see cref="INexusDuplexPipe"/> from which to create the duplex channel.</param>
    /// <returns>A task result which contains the <see cref="INexusDuplexUnmanagedChannel{T}"/>.</returns>
    /// <remarks>
    /// This method is optimized for unmanaged types and should be used over the non-unmanaged version when possible.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static INexusDuplexUnmanagedChannel<T> GetUnmanagedChannel<T>(this INexusDuplexPipe pipe)
        where T : unmanaged
    {
        return new NexusDuplexUnmanagedChannel<T>(pipe);
    }

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
    /// Asynchronously creates and returns a channel reader for unmanaged types.
    /// See: https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/builtin-types/unmanaged-types
    /// </summary>
    /// <typeparam name="T">The type of unmanaged data to be read from the <see cref="INexusDuplexPipe"/></typeparam>
    /// <param name="pipe"><see cref="INexusDuplexPipe"/> from which to create the channel reader.</param>
    /// <returns>The task containing the unmanaged channel reader.</returns>
    /// <remarks>
    /// This is optimized for unmanaged types and should be used over the non-unmanaged version when possible.
    /// Will await <see cref="INexusDuplexPipe.ReadyTask"/> to ensure the pipe is ready.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static async ValueTask<INexusChannelReader<T>> GetUnmanagedChannelReader<T>(this INexusDuplexPipe pipe)
        where T : unmanaged
    {
        await pipe.ReadyTask.ConfigureAwait(false);
        return new NexusChannelReaderUnmanaged<T>(pipe.ReaderCore);
    }


    /// <summary>
    /// Asynchronously creates and returns a channel writer for unmanaged types.
    /// See: https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/builtin-types/unmanaged-types
    /// </summary>
    /// <typeparam name="T">The type of unmanaged data to write to the <see cref="INexusDuplexPipe"/></typeparam>
    /// <param name="pipe"><see cref="INexusDuplexPipe"/> from which to create the channel writer.</param>
    /// <returns>The task containing the unmanaged channel writer.</returns>
    /// <remarks>
    /// This is optimized for unmanaged types and should be used over the non-unmanaged version when possible.
    /// Will await <see cref="INexusDuplexPipe.ReadyTask"/> to ensure the pipe is ready.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static async ValueTask<INexusChannelWriter<T>> GetUnmanagedChannelWriter<T>(this INexusDuplexPipe pipe)
        where T : unmanaged
    {
        await pipe.ReadyTask.ConfigureAwait(false);
        return new NexusChannelWriterUnmanaged<T>(pipe.WriterCore);
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
}
