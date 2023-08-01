using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using MemoryPack;

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

    [MemoryPackOrder(0)]
    public int InvocationId { get; set; }

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

    [MemoryPackOnDeserialized]
    private void OnDeserialized()
    {
        _isArgumentPoolArray = true;
    }

    public void Reset()
    {
        if (!_isArgumentPoolArray)
            return;

        // Reset the pool flag.
        _isArgumentPoolArray = false;

        IMessageBase.Return(Arguments); 
        Arguments = default;
    }
}
