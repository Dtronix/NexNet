using System;
using System.Runtime.CompilerServices;
using MemoryPack;

namespace NexNet.Messages;

[MemoryPackable]
internal partial class DuplexPipeUpdateStateMessage : IMessageBase
{
    public static MessageType Type { get; } = MessageType.DuplexPipeUpdateState;

    public ushort PipeId { get; set; }

    public State StateFlags { get; set; }

    public (byte ClientId, byte ServerId) GetClientAndServerId()
    {
        Span<byte> bytes = stackalloc byte[sizeof(ushort)];
        Unsafe.As<byte, ushort>(ref bytes[0]) = PipeId;
        return (bytes[0], bytes[1]);
    }

}
