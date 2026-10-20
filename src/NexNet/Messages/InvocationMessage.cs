using System;
using System.Runtime.CompilerServices;
using System.Threading;
using NexNet.Pools;
using NexNet.Serialization;

namespace NexNet.Messages;

/// <summary>
/// Contains an invocation request message data.
/// Body: <c>[invocationId (uint16), methodId (uint16), flags (uint8), arguments]</c>.
/// The arguments are an embedded MessagePack array.
/// </summary>
internal partial class InvocationMessage : IMessageBase, IInvocationMessage
{
    /// <summary>
    /// True if the arguments were deserialized into a pooled array.
    /// </summary>
    private bool _isArgumentPoolArray;
    public static MessageType Type { get; } = MessageType.Invocation;

    private IPooledMessage? _messageCache = null!;
    private NexusSerializerOptions _options = NexusSerializerOptions.Untrusted;

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

    /// <summary>
    /// Receiving side: the execution slot this invocation runs in. A pipe or collection method frees it when its pipe
    /// registers, so long-lived pipes do not hold slots.
    /// </summary>
    internal NexNet.Internals.IInvocationSlot? Slot;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public T? DeserializeArguments<T>()
    {
        var reader = new MsgPackReader(Arguments, _options);
        T? value = default;
        NexusFormatterRegistry.Get<T>().Deserialize(ref reader, ref value);
        return value;
    }

    /// <summary>
    /// Sets the pre-serialized argument bytes on this message.
    /// </summary>
    /// <param name="serializedArguments">Pre-serialized argument bytes.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetArguments(Memory<byte> serializedArguments)
    {
        Arguments = serializedArguments;
    }

    public void Serialize(ref MsgPackWriter writer)
    {
        new InvocationBody(this).Write(ref writer);
    }

    public void Deserialize(ref MsgPackReader reader)
    {
        reader.ReadArrayHeader(4);
        InvocationId = reader.ReadUInt16();
        MethodId = reader.ReadUInt16();
        Flags = (InvocationFlags)reader.ReadByte();
        _options = reader.Options;
        // An empty argument array (0x90) is the wire form of "no serialized arguments".
        if (reader.PeekCode() == MsgPackCode.MinFixArray)
        {
            reader.ReadArrayHeader();
            Arguments = Memory<byte>.Empty;
            _isArgumentPoolArray = false;
            return;
        }

        Arguments = IMessageBase.ReadEmbeddedValueToPooled(ref reader);
        _isArgumentPoolArray = true;
    }

    // Budget reserved for a message reassembled from a large stream; released when the message is disposed.
    private NexNet.Internals.SessionBudget? _reservedFrom;
    private long _reservedBytes;

    /// <summary>
    /// Makes this message hold <paramref name="bytes"/> of <paramref name="budget"/> until it is disposed.
    /// </summary>
    internal void ReserveFrom(NexNet.Internals.SessionBudget budget, long bytes)
    {
        _reservedBytes = bytes;
        _reservedFrom = budget;
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _reservedFrom, null)?.Release(_reservedBytes);
        Interlocked.Exchange(ref ArgumentsOwner, null)?.Return();
        Slot = null;

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
