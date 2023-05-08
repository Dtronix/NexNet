using System;

namespace NexNet.Messages;

/// <summary>
/// Message interface for invocations.
/// </summary>
public interface IInvocationRequestMessage
{
    /// <summary>
    /// Max length allowed: ushort.MaxValue - (Type:byte) - (InvocationId:int) - (MethodId:ushort) - (Flags:byte) = 65527;
    /// </summary>
    public const int MaxArgumentSize = 65519;/*ushort.MaxValue
                                         - sizeof(int) // InvocationId
                                         - sizeof(ushort) // MethodId
                                         - sizeof(InvocationFlags) // Flags
                                         - sizeof(MessageType) // header Type
                                         - 2; // BodyLength*/
    /// <summary>
    /// Unique invocation ID.
    /// </summary>
    int InvocationId { get; set; }

    /// <summary>
    /// Method ID to invoke.
    /// </summary>
    ushort MethodId { get; set; }

    /// <summary>
    /// Invocation configuration flags.
    /// </summary>
    InvocationFlags Flags { get; set; }

    /// <summary>
    /// Arguments 
    /// </summary>
    Memory<byte> Arguments { get; set; }
}
