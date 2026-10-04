namespace NexNet.Internals;

/// <summary>
/// Identifies the serializer used for user payloads. Sent in byte [4] of the protocol header so that
/// peers built with different payload serializers refuse each other instead of failing to deserialize.
/// </summary>
internal enum PayloadFormat : byte
{
    /// <summary>
    /// Not set. Never valid on the wire.
    /// </summary>
    Unset = 0,

    /// <summary>
    /// User payloads are serialized with MemoryPack.
    /// </summary>
    MemoryPack = 1,

    /// <summary>
    /// User payloads are serialized with the NexNet MessagePack serializer.
    /// </summary>
    MessagePack = 2,
}

internal static class PayloadFormatInfo
{
#if NEXNET_MEMORYPACK
    public const PayloadFormat Local = PayloadFormat.MemoryPack;
#else
    public const PayloadFormat Local = PayloadFormat.MessagePack;
#endif
}
