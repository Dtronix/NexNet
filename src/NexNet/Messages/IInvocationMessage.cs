using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace NexNet.Messages;

/// <summary>
/// Message interface for invocations.
/// </summary>
public interface IInvocationMessage
{
    /// <summary>
    /// Maximum serialized argument size. The message body is limited to <see cref="ushort.MaxValue"/> bytes and the
    /// worst-case MessagePack overhead of an invocation body is 9 bytes: fixarray header (1) + uint16 invocation ID (3)
    /// + uint16 method ID (3) + uint8 flags (2). With the MemoryPack payload format the arguments are additionally
    /// wrapped in a bin16 header (3).
    /// </summary>
#if NEXNET_MEMORYPACK
    public const int MaxArgumentSize = ushort.MaxValue - 9 - 3;
#else
    public const int MaxArgumentSize = ushort.MaxValue - 9;
#endif
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
    T? DeserializeArguments<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] T>();
}
