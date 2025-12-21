using System;
using System.Runtime.CompilerServices;
using System.Threading;
using MemoryPack;
using NexNet.Pools;

namespace NexNet.Messages;

/// <summary>
/// Contains an invocation request message data.
/// </summary>
[MemoryPackable(SerializeLayout.Explicit)]
internal partial class InvocationMessage : IMessageBase, IInvocationMessage
{
    /// <summary>
    /// True if the message was deserialized from a pool.
    /// </summary>
    private bool _isArgumentPoolArray;
    public static MessageType Type { get; } = MessageType.Invocation;

    private IPooledMessage? _messageCache = null!;

    [MemoryPackIgnore]
    public IPooledMessage? MessageCache
    {
        set => _messageCache = value;
    }

    [MemoryPackOrder(0)]
    public ushort InvocationId { get; set; }

    [MemoryPackOrder(1)]
    public ushort MethodId { get; set; }

    [MemoryPackOrder(2)]
    public InvocationFlags Flags { get; set; } = InvocationFlags.None;

    [MemoryPackOrder(3)]
    [MemoryPoolFormatter<byte>]
    public Memory<byte> Arguments { get; set; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public T? DeserializeArguments<T>()
    {
        return MemoryPackSerializer.Deserialize<T>(Arguments.Span);
    }
    public bool TrySetArguments(ITuple? arguments)
    {
        Arguments = arguments == null
            ? Memory<byte>.Empty
            : MemoryPackSerializer.Serialize(arguments.GetType(), arguments);

        //TODO: Review this on the sync path as it will get ignored as it is running on a separate task from the original caller.
        return Arguments.Length <= IInvocationMessage.MaxArgumentSize;
    }

    /*
    TODO: Review custom serialization for the arguments.
    [MemoryPackOnSerializing]
    static void WriteArguments<TBufferWriter>(ref MemoryPackWriter<TBufferWriter> writer, ref InvocationMessage? value)
        where TBufferWriter : class, IBufferWriter<byte>
    {
        var initialLength = writer.WriteVarInt();
        MemoryPackSerializer.Serialize(value._writeArguments.GetType(), ref writer, value._writeArguments);
        ;
    }

    [MemoryPackOnDeserializing]
    static void ReadArguments(ref MemoryPackReader reader, ref InvocationMessage? value)
    {
        MemoryPackSerializer.Deserialize()
        // read custom header before deserialize
        var guid = reader.ReadUnmanaged<Guid>();
        Console.WriteLine(guid);
    }*/

    [MemoryPackOnDeserialized]
    private void OnDeserialized()
    {
        _isArgumentPoolArray = true;
    }

    public void Dispose()
    {
        var cache = Interlocked.Exchange(ref _messageCache, null);

        if (cache == null)
            return;

        if (_isArgumentPoolArray)
        {
            // Reset the pool flag.
            _isArgumentPoolArray = false;
            IMessageBase.ReturnMemoryPackMemory(Arguments);
            Arguments = default;
        }

        cache.Return(this);
    }
}
