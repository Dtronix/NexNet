using System.Buffers;
using NexNet.Messages;

namespace NexNet.Cache;

/// <summary>
/// Defines the operations for a cache that stores and manages messages.
/// </summary>
internal interface ICachedMessage
{
    /// <summary>
    /// Clears all the messages from the cache.
    /// </summary>
    void Clear();

    /// <summary>
    /// Deserializes the given sequence of bytes into a message.
    /// </summary>
    /// <param name="bodySequence">The sequence of bytes to be deserialized.</param>
    /// <returns>The deserialized message.</returns>
    IMessageBase DeserializeInterface(in ReadOnlySequence<byte> bodySequence);


    /// <summary>
    /// Returns the specified message item back to the cache.
    /// </summary>
    /// <param name="item">The message item to be returned to the cache.</param>
    void Return(IMessageBase item);
}
