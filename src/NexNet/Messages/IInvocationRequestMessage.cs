using System;

namespace NexNet.Messages;

/// <summary>
/// Message interface for invocations.
/// </summary>
public interface IInvocationRequestMessage
{
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
