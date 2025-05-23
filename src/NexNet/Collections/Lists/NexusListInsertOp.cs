using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using MemoryPack;
using NexNet.Messages;

namespace NexNetSample.Asp.Shared;

[MemoryPackable]
[MemoryPackUnion(0, typeof(NexusListInsertOperation))]
public partial interface INexusListOperation
{
    
}


/// <summary>
/// Contains an invocation request message data.
/// </summary>
[MemoryPackable(SerializeLayout.Explicit)]
public partial class NexusListInsertOperation : INexusListOperation
{
    internal bool IsArgumentPoolArray;

    [MemoryPackOrder(0)]
    public int Index { get; set; }
    
    [MemoryPackOrder(1)]
    [MemoryPoolFormatter<byte>]
    public Memory<byte> Value { get; set; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public T? DeserializeValue<T>()
    {
        return MemoryPackSerializer.Deserialize<T>(Value.Span);
    }
    
    public bool TrySetArguments(ITuple? arguments)
    {
        Value = arguments == null
            ? Memory<byte>.Empty
            : MemoryPackSerializer.Serialize(arguments.GetType(), arguments);

        //TODO: Review this on the sync path as it will get ignored as it is running on a separate task from the original caller.
        return Value.Length <= IInvocationMessage.MaxArgumentSize;
    }

    [MemoryPackOnDeserialized]
    private void OnDeserialized()
    {
        IsArgumentPoolArray = true;
    }

    public void Dispose()
    {
        var cache = Interlocked.Exchange(ref _messageCache, null);

        if (cache == null)
            return;

        if (IsArgumentPoolArray)
        {
            // Reset the pool flag.
            IsArgumentPoolArray = false;
            if (MemoryMarshal.TryGetArray<byte>(Value, out var segment) && segment.Array is { Length: > 0 })
                ArrayPool<byte>.Shared.Return(segment.Array, false);
            
            Value = default;
        }

        cache.Return(this);
    }
}
