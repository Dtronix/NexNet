using System.Threading.Tasks;

namespace NexNet.Pipes;

/// <summary>
/// Provides extension methods for the <see cref="INexusDuplexPipe"/> interface.
/// These methods allow for the creation of channel readers and writers, both for managed and unmanaged types.
/// </summary>
public static class NexusDuplexPipeExtensions
{
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
    public static async ValueTask<INexusChannelReader<T>> GetUnmanagedChannelReader<T>(this INexusDuplexPipe pipe)
        where T : unmanaged
    {
        await pipe.ReadyTask;
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
    public static async ValueTask<INexusChannelWriter<T>> GetUnmanagedChannelWriter<T>(this INexusDuplexPipe pipe)
        where T : unmanaged
    {
        await pipe.ReadyTask;
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
    public static async ValueTask<INexusChannelReader<T>> GetChannelReader<T>(this INexusDuplexPipe pipe)
    {
        await pipe.ReadyTask;
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
    public static async ValueTask<INexusChannelWriter<T>> GetChannelWriter<T>(this INexusDuplexPipe pipe)
    {
        await pipe.ReadyTask;
        return new NexusChannelWriter<T>(pipe.WriterCore);
    }
}
