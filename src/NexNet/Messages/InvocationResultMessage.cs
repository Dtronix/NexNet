using System;
using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using NexNet.Pools;
using NexNet.Serialization;

namespace NexNet.Messages;

/// <summary>
/// Result of an invocation.
/// Body: <c>[invocationId (uint16), state (uint8)]</c> when there is no result, or
/// <c>[invocationId (uint16), state (uint8), result]</c> when a result is present (the result may itself be nil).
/// With the MessagePack payload format the result is an embedded MessagePack value; with the MemoryPack payload
/// format it is a bin value holding the MemoryPack bytes.
/// </summary>
internal partial class InvocationResultMessage : IMessageBase
{
    public enum StateType : byte
    {
        Unset = 0,
        CompletedResult = 1,
        Exception = 2,
        Unauthorized = 3
    }

    public static MessageType Type { get; } = MessageType.InvocationResult;

    private IPooledMessage? _messageCache = null!;
    private ReadOnlySequence<byte>? _result;
    private Memory<byte> _pooledResult;
    private NexusSerializerOptions _options = NexusSerializerOptions.Untrusted;

    public IPooledMessage? MessageCache
    {
        set => _messageCache = value;
    }

    public ushort InvocationId { get; set; }

    public StateType State { get; set; }

    public ReadOnlySequence<byte>? Result
    {
        get => _result;
        set => _result = value;
    }

    public bool TryGetResult<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] T>(out T? result)
    {
        if (_result == null)
        {
            result = default;
            return false;
        }

#if NEXNET_MEMORYPACK
        result = MemoryPack.MemoryPackSerializer.Deserialize<T>(_result.Value);
#else
        var reader = new MsgPackReader(_result.Value, _options);
        result = default;
        NexusFormatterRegistry.Get<T>().Deserialize(ref reader, ref result);
#endif
        return true;
    }

    public void Serialize(ref MsgPackWriter writer)
    {
        if (_result == null)
        {
            writer.WriteArrayHeader(2);
            writer.Write(InvocationId);
            writer.Write((byte)State);
            return;
        }

        writer.WriteArrayHeader(3);
        writer.Write(InvocationId);
        writer.Write((byte)State);
#if NEXNET_MEMORYPACK
        writer.WriteBinary(_result.Value);
#else
        writer.WriteRaw(_result.Value);
#endif
    }

    public void Deserialize(ref MsgPackReader reader)
    {
        var count = reader.ReadArrayHeader();
        if (count != 2 && count != 3)
            throw NexusSerializationException.UnexpectedCount(3, count);

        InvocationId = reader.ReadUInt16();
        State = (StateType)reader.ReadByte();
        _options = reader.Options;

        if (count == 2)
        {
            _result = null;
            return;
        }

#if NEXNET_MEMORYPACK
        _pooledResult = reader.ReadBinaryToPooled(out _);
#else
        _pooledResult = IMessageBase.ReadEmbeddedValueToPooled(ref reader);
#endif
        _result = new ReadOnlySequence<byte>(_pooledResult);
    }

    public void Dispose()
    {
        var cache = Interlocked.Exchange(ref _messageCache, null);

        if (cache == null)
            return;

        _result = null;
        if (!_pooledResult.IsEmpty)
        {
            IMessageBase.ReturnPooledMemory(_pooledResult);
            _pooledResult = default;
        }

        cache.Return(this);
    }
}
