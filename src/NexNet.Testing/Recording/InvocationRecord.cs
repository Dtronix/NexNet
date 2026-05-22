using System;
using System.Reflection;

namespace NexNet.Testing.Recording;

/// <summary>
/// One row captured by <see cref="InvocationRecorder"/>: which method id the session received
/// and the raw serialized argument bytes. The bytes stay serialized until an assertion needs
/// to inspect them; this keeps the dispatch hot path zero-cost beyond a small allocation per
/// recorded invocation.
/// </summary>
internal sealed class InvocationRecord
{
    public ushort MethodId { get; }
    public ReadOnlyMemory<byte> Arguments { get; }

    /// <summary>
    /// Method id resolved to a CLR <see cref="MethodInfo"/>, when known. Populated by the
    /// host's method-id map at recorder construction time; null if the method id does not
    /// belong to a known nexus interface.
    /// </summary>
    public MethodInfo? Method { get; }

    public InvocationRecord(ushort methodId, ReadOnlyMemory<byte> arguments, MethodInfo? method)
    {
        MethodId = methodId;
        Arguments = arguments;
        Method = method;
    }
}
