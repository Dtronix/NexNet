using System;
using MemoryPack;

namespace NexNet.Messages;

/// <summary>
/// Contains an invocation request message data.
/// </summary>
[MemoryPackable]
public partial class InvocationRequestMessage : IMessageBodyBase
{
    /// <summary>
    /// Flags to configure how the invocation is to be configured.
    /// </summary>
    [Flags]
    public enum InvocationFlags : byte
    {
        /// <summary>
        /// No special configurations
        /// </summary>
        None = 0,

        /// <summary>
        /// Instructs to ignore the return value if there is one.
        /// </summary>
        IgnoreReturn = 1
    }

    /// <summary>
    /// Type of this message used for deserialization.
    /// </summary>
    public static MessageType Type { get; } = MessageType.InvocationWithResponseRequest;

    /// <summary>
    /// Unique invocation ID.
    /// </summary>
    public int InvocationId { get; set; }

    /// <summary>
    /// Method ID to invoke.
    /// </summary>
    public ushort MethodId { get; set; }

    /// <summary>
    /// Invocation configuration flags.
    /// </summary>
    public InvocationFlags Flags { get; set; } = InvocationFlags.None;

    /// <summary>
    /// Arguments 
    /// </summary>
    public Memory<byte> Arguments { get; set; }
}
