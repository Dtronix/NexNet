using System;
using MemoryPack;

namespace NexNet.Messages;

[MemoryPackable]
internal partial class PipeCompleteMessage : IMessageBase
{
    [Flags]
    public enum Flags : byte
    {
        Unset = 0,
        Complete = 1 << 0,
        Canceled = 1 << 1
    }

    public static MessageType Type { get; } = MessageType.PipeComplete;
    public MessageType MessageType => Type;

    public int InvocationId { get; set; }
    public Flags CompleteFlags { get; set; }
}
