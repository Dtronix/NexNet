using System;
using System.Runtime.CompilerServices;
using System.Threading;
using MemoryPack;
using NexNet.Cache;

namespace NexNet.Messages;

/// <summary>
/// Contains an invocation request message data.
/// </summary>
[MemoryPackable(SerializeLayout.Explicit)]
internal partial class CollectionUpdateMessage : IMessageBase
{
    /// <summary>
    /// True if the message was deserialized from a pool.
    /// </summary>
    private bool _isArgumentPoolArray;
    public static MessageType Type { get; } = MessageType.CollectionUpdate;

    private ICachedMessage? _messageCache = null!;

    [MemoryPackIgnore]
    public ICachedMessage? MessageCache
    {
        set => _messageCache = value;
    }

    [MemoryPackOrder(0)]
    public ushort CollectionId { get; set; }
    
    [MemoryPackOrder(1)]
    public ushort StateId { get; set; }

    [MemoryPackOrder(2)]
    public ChangeType Change { get; set; }

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

    public enum ChangeType
    {
        Unset,
        Insert,
        Remove,
        Update,
        Reset
    }
}
