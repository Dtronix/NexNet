using System.Collections.Immutable;

namespace NexNet.Generator.Models;

/// <summary>
/// Data for a single method in a nexus interface.
/// </summary>
internal sealed record MethodData(
    // Identification
    string Name,
    ushort Id,                          // Assigned or auto-generated

    // Method characteristics
    bool IsStatic,
    bool IsAsync,
    bool IsReturnVoid,

    // Return type info
    string? ReturnType,                 // Full type string
    string? ReturnTypeSource,           // MinimallyQualifiedFormat
    int ReturnArity,                    // Generic arity of return type

    // Parameters
    ImmutableArray<MethodParameterData> Parameters,
    int SerializedParameterCount,

    // Special parameters (indices into Parameters, -1 if not present)
    int CancellationTokenParameterIndex,
    int DuplexPipeParameterIndex,

    // Validation flags
    bool UtilizesPipes,
    bool MultiplePipeParameters,
    bool MultipleCancellationTokenParameters,

    // Hash for version checking
    int NexusHash,

    // Attribute data
    NexusMethodAttributeData MethodAttribute,

    // For diagnostics
    LocationData? Location
)
{
    public MethodParameterData? CancellationTokenParameter =>
        CancellationTokenParameterIndex >= 0 ? Parameters[CancellationTokenParameterIndex] : null;

    public MethodParameterData? DuplexPipeParameter =>
        DuplexPipeParameterIndex >= 0 ? Parameters[DuplexPipeParameterIndex] : null;
}
