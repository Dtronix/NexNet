using System;

namespace NexNet.Diagnostics;

/// <summary>
/// A generated nexus that carries its inspector schema: the generator's IR of the nexus pair (methods with ids,
/// parameter and return types, pipes and channels, collections, versions and every serialized type they reach)
/// serialized as MessagePack, the <c>.nnschema</c> format. Tools use it to decode captured traffic without reflection.
/// </summary>
/// <remarks>
/// Implemented only when the project sets the MSBuild property <c>NexNetEmitInspectorSchema</c> to <c>true</c>. The
/// members are implemented explicitly, so they are reached through a generic constraint:
/// <c>static ReadOnlySpan&lt;byte&gt; Schema&lt;T&gt;() where T : IInspectorSchemaSource =&gt; T.InspectorSchema;</c>.
/// The layout is documented in docs/internals/nexnet-ir.md.
/// </remarks>
public interface IInspectorSchemaSource
{
    /// <summary>
    /// The schema bytes. They live in the assembly's data section: reading them does not allocate.
    /// </summary>
    static abstract ReadOnlySpan<byte> InspectorSchema { get; }

    /// <summary>
    /// 64-bit FNV-1a hash of <see cref="InspectorSchema"/>, computed at generation time, so two schemas can be compared
    /// (or a cached copy looked up) without comparing the bytes.
    /// </summary>
    static abstract ulong InspectorSchemaHash { get; }
}
