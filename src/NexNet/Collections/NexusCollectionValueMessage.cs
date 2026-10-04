using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using NexNet.Internals;
using NexNet.Serialization;

namespace NexNet.Collections;

internal abstract class NexusCollectionValueMessage<TMessage, TUnion> : NexusCollectionMessage<TMessage, TUnion>
    where TMessage : NexusCollectionMessage<TMessage, TUnion>, TUnion, new()
    where TUnion : class, INexusCollectionUnion<TUnion>
{
    private bool _isArgumentPoolArray;
    private NexusSerializerOptions _options = NexusSerializerOptions.Untrusted;

    protected Memory<byte> ValueCore;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TValue? DeserializeValue<TValue>()
    {
        return PayloadSerializer.Deserialize<TValue>(ValueCore, _options);
    }

    /// <summary>
    /// Reads the embedded value into a pooled buffer that is returned with the message.
    /// </summary>
    protected void ReadValueCore(ref MsgPackReader reader)
    {
        _options = reader.Options;
        ValueCore = PayloadSerializer.ReadEmbeddedToPooled(ref reader);
        _isArgumentPoolArray = true;
    }

    public override void Return()
    {
        ReturnValueToPool();
        base.Return();
    }

    public void ReturnValueToPool()
    {
        if (_isArgumentPoolArray)
        {
            // Reset the pool flag.
            _isArgumentPoolArray = false;
            if (MemoryMarshal.TryGetArray<byte>(ValueCore, out var segment) && segment.Array is { Length: > 0 })
                ArrayPool<byte>.Shared.Return(segment.Array, false);

            ValueCore = default;
        }
    }
}
