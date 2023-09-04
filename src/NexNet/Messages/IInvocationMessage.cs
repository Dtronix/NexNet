using System;
using System.Runtime.CompilerServices;

namespace NexNet.Messages;

/// <summary>
/// Message interface for invocations.
/// </summary>
public interface IInvocationMessage
{
    /// <summary>
    /// Max length allowed: ushort.MaxValue - (Type:byte) - (InvocationId:int) - (MethodId:ushort) - (Flags:byte) = 65527;
    /// </summary>
    public const int MaxArgumentSize = 65521;/*ushort.MaxValue
                                         - sizeof(ushort) // InvocationId
                                         - sizeof(ushort) // MethodId
                                         - sizeof(InvocationFlags) // Flags
                                         - sizeof(MessageType) // header Type
                                         - 2; // BodyLength*/
    /// <summary>
    /// Unique invocation ID.
    /// </summary>
    ushort InvocationId { get; set; }

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

    /// <summary>
    /// Deserializes the arguments to the specified type.
    /// </summary>
    /// <typeparam name="T">Type to deserialize to.</typeparam>
    /// <returns>Deserialized value</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    T? DeserializeArguments<T>();
}
