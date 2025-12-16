namespace NexNet.Generator.Models;

/// <summary>
/// Data for a single method parameter.
/// </summary>
internal sealed record MethodParameterData(
    string Name,
    string Type,                        // Full type string (ParamType)
    string TypeSource,                  // MinimallyQualifiedFormat (ParamTypeSource)
    string? SerializedType,             // Type for serialization (null if not serialized)
    string? SerializedValue,            // Value expression for serialization
    int Index,                          // Position in parameter list
    int SerializedId,                   // ID for serialization (0 if not serialized)
    bool IsCancellationToken,
    bool IsDuplexPipe,                  // INexusDuplexPipe parameter
    bool IsDuplexUnmanagedChannel,      // INexusDuplexUnmanagedChannel<T> parameter
    bool IsDuplexChannel,               // INexusDuplexChannel<T> parameter
    bool UtilizesDuplexPipe,            // Any duplex pipe type
    string? ChannelType,                // Generic type argument for channel types
    int NexusHashCode                   // Hash contribution
);
