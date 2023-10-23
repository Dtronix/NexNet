using System.Buffers;
using System.Threading;
using MemoryPack;
using NexNet.Cache;

namespace NexNet.Messages;

[MemoryPackable(SerializeLayout.Explicit)]
internal partial class InvocationResultMessage : IMessageBase
{
    public enum StateType : byte
    {
        Unset = 0,
        CompletedResult = 1,
        Exception = 2
    }

    public static MessageType Type { get; } = MessageType.InvocationResult;

    private ICachedMessage? _messageCache = null!;
    private ReadOnlySequence<byte>? _result;

    [MemoryPackIgnore]
    public ICachedMessage? MessageCache
    {
        set => _messageCache = value;
    }

    [MemoryPackOrder(0)]
    public ushort InvocationId { get; set; }

    [MemoryPackOrder(1)]
    public StateType State { get; set; }

    [MemoryPackOrder(2)]
    public ReadOnlySequence<byte>? Result
    {
        get => _result;
        set => _result = value;
    }

    public bool TryGetResult<T>(out T? result)
    {
        if (_result == null)
        {
            result = default;
            return false;
        }

        result = MemoryPackSerializer.Deserialize<T>(_result.Value);
        return true;
    }

    public void Dispose()
    {
        var cache = Interlocked.Exchange(ref _messageCache, null);

        if (cache == null)
            return;

        _result = null;

        cache.Return(this);
    }
}
