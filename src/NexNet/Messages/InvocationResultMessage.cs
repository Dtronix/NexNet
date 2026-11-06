using System;
using System.Buffers;
using System.Threading;
using NexNet.Pools;
using NexNet.Serialization;

namespace NexNet.Messages;

/// <summary>
/// Result of an invocation.
/// Body: <c>[invocationId (uint16), state (uint8)]</c> when there is no result, or
/// <c>[invocationId (uint16), state (uint8), result]</c> when a result is present (the result may itself be nil).
/// The result is an embedded MessagePack value. For the Exception state it is the optional
/// <see cref="RemoteErrorDetail"/>.
/// </summary>
internal partial class InvocationResultMessage : IMessageBase
{
    public enum StateType : byte
    {
        Unset = 0,
        CompletedResult = 1,
        Exception = 2,
        Unauthorized = 3,
        Canceled = 4
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

    public bool TryGetResult<T>(out T? result)
    {
        if (_result == null)
        {
            result = default;
            return false;
        }

        var reader = new MsgPackReader(_result.Value, _options);
        result = default;
        NexusFormatterRegistry.Get<T>().Deserialize(ref reader, ref result);
        return true;
    }

    /// <summary>
    /// Reads the remote error detail of an Exception result. False when there is none or it is not valid.
    /// </summary>
    public bool TryGetRemoteError(out int? code, out string? type, out string? message)
    {
        if (_result == null)
        {
            code = null;
            type = null;
            message = null;
            return false;
        }

        return RemoteErrorDetail.TryRead(_result.Value, _options, out code, out type, out message);
    }

    public void Serialize(ref MsgPackWriter writer) => new InvocationResultBody(this).Write(ref writer);

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

        _pooledResult = IMessageBase.ReadEmbeddedValueToPooled(ref reader);
        _result = new ReadOnlySequence<byte>(_pooledResult);
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
