using System;
using MemoryPack;

namespace NexNet.Messages;

[MemoryPackable]
internal partial class PipeReadyMessage : IMessageBase
{
    public static MessageType Type { get; } = MessageType.PipeReady;

    public int InvocationId { get; set; }
}
