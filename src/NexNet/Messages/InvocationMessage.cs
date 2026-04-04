using System;
using System.Diagnostics.CodeAnalysis;
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
    public T? DeserializeArguments<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] T>()
    {
        return MemoryPackSerializer.Deserialize<T>(Arguments.Span);
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
