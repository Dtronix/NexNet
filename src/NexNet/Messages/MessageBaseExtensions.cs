using System.Runtime.CompilerServices;

namespace NexNet.Messages;

/// <summary>
/// Extensions for <see cref="IMessageBase"/>.
/// </summary>
internal static class MessageBaseExtensions
{

    /// <summary>
    /// Casts in an unsafe way the message to the specified type. Use only on hot paths.
    /// </summary>
    /// <typeparam name="T">Type to cast the message to.</typeparam>
    /// <param name="message">Message to cast.</param>
    /// <returns>Cast message.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static T As<T>(this IMessageBase message)
        where T : class, IMessageBase
    {
        return Unsafe.As<T>(message);
    }
}
