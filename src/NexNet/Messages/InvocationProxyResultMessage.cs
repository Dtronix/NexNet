﻿using System;
using System.Buffers;
using MemoryPack;

namespace NexNet.Messages;

[MemoryPackable]
internal partial class InvocationProxyResultMessage : IMessageBodyBase
{
    public enum StateType : byte
    {
        Unset = 0,
        CompletedResult = 3,
        Exception = 5
    }

    public static MessageType Type { get; } = MessageType.InvocationProxyResult;

    public int InvocationId { get; set; }

    public StateType State { get; set; }

    public ReadOnlySequence<byte>? Result { get; set; }

    public T? GetResult<T>()
    {
        if (Result == null)
            return default;

        return MemoryPackSerializer.Deserialize<T>(Result.Value);
    }

    public object? GetResult(Type? type)
    {
        if (Result == null || type == null)
            return default;

        return MemoryPackSerializer.Deserialize(type, Result.Value);
    }
}
