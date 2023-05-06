using System;
using MemoryPack;

namespace NexNet.Messages;

/// <summary>
/// Contains an invocation request message data.
/// </summary>
[MemoryPackable]
internal partial class InvocationRequestMessage : IMessageBodyBase, IInvocationRequestMessage
{



    public static MessageType Type { get; } = MessageType.InvocationWithResponseRequest;

    public int InvocationId { get; set; }

    public ushort MethodId { get; set; }

    public InvocationFlags Flags { get; set; } = InvocationFlags.None;

    public Memory<byte> Arguments { get; set; }
}
