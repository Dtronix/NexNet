using System;
using MemoryPack;

namespace NexNet.Messages;

[MemoryPackable]
public partial class InvocationRequestMessage : IMessageBodyBase
{
    [Flags]
    public enum InvocationFlags : byte
    {
        None = 0,
        IgnoreReturn = 1
    }

    public static MessageType Type { get; } = MessageType.InvocationWithResponseRequest;

    /// <summary>
    /// Unique invocation ID.
    /// </summary>
    public int InvocationId { get; set; }

    public ushort MethodId { get; set; }

    public InvocationFlags Flags { get; set; } = InvocationFlags.None;

    public Memory<byte> Arguments { get; set; }



}
