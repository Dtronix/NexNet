using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading;
using NexNet.Pools;
using NexNet.Serialization;

namespace NexNet.Messages;

/// <summary>
/// Contains an invocation request message data.
/// Body: <c>[invocationId (uint16), methodId (uint16), flags (uint8), arguments]</c>.
/// With the MessagePack payload format the arguments are an embedded MessagePack array; with the MemoryPack payload
/// format they are a bin value holding the MemoryPack bytes.
/// </summary>
internal partial class InvocationMessage : IMessageBase, IInvocationMessage
{
    /// <summary>
    /// True if the arguments were deserialized into a pooled array.
    /// </summary>
    private bool _isArgumentPoolArray;
    public static MessageType Type { get; } = MessageType.Invocation;

    private IPooledMessage? _messageCache = null!;

    public IPooledMessage? MessageCache
    {
        set => _messageCache = value;
    }

    public ushort InvocationId { get; set; }

    public ushort MethodId { get; set; }

    public InvocationFlags Flags { get; set; } = InvocationFlags.None;

    public Memory<byte> Arguments { get; set; }

    /// <summary>
    /// Pooled buffer that backs <see cref="Arguments"/> on the sending side; returned to its pool when this message
    /// is disposed (after it has been sent to every target).
    /// </summary>
    internal PooledArrayBufferWriter? ArgumentsOwner;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public T? DeserializeArguments<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] T>()
    {
#if NEXNET_MEMORYPACK
        return MemoryPack.MemoryPackSerializer.Deserialize<T>(Arguments.Span);
#else
        var reader = new MsgPackReader(Arguments);
        T? value = default;
        NexusFormatterRegistry.Get<T>().Deserialize(ref reader, ref value);
        return value;
#endif
    }

    /// <summary>
    /// Sets the pre-serialized argument bytes on this message.
    /// </summary>
    /// <param name="serializedArguments">Pre-serialized argument bytes.</param>
    /// <returns>True if the arguments fit within the maximum size.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TrySetArguments(Memory<byte> serializedArguments)
    {
        Arguments = serializedArguments;
        return Arguments.Length <= IInvocationMessage.MaxArgumentSize;
    }

    public void Serialize(ref MsgPackWriter writer)
    {
        writer.WriteArrayHeader(4);
        writer.Write(InvocationId);
        writer.Write(MethodId);
        writer.Write((byte)Flags);
#if NEXNET_MEMORYPACK
        writer.WriteBinary(Arguments.Span);
#else
        if (Arguments.IsEmpty)
            writer.WriteArrayHeader(0); // methods without serialized parameters still send an empty array
        else
            writer.WriteRaw(Arguments.Span); // already a MessagePack array produced by generated code
#endif
    }

    public void Deserialize(ref MsgPackReader reader)
    {
        reader.ReadArrayHeader(4);
        InvocationId = reader.ReadUInt16();
        MethodId = reader.ReadUInt16();
        Flags = (InvocationFlags)reader.ReadByte();
#if NEXNET_MEMORYPACK
        Arguments = reader.ReadBinaryToPooled(out _);
#else
        // An empty argument array (0x90) is the wire form of "no serialized arguments".
        if (reader.PeekCode() == MsgPackCode.MinFixArray)
        {
            reader.ReadArrayHeader();
            Arguments = Memory<byte>.Empty;
            _isArgumentPoolArray = false;
            return;
        }

        Arguments = IMessageBase.ReadEmbeddedValueToPooled(ref reader);
#endif
        _isArgumentPoolArray = true;
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref ArgumentsOwner, null)?.Return();

        var cache = Interlocked.Exchange(ref _messageCache, null);

        if (cache == null)
            return;

        if (_isArgumentPoolArray)
        {
            // Reset the pool flag.
            _isArgumentPoolArray = false;
            IMessageBase.ReturnPooledMemory(Arguments);
        }

        Arguments = default;
        cache.Return(this);
    }
}
