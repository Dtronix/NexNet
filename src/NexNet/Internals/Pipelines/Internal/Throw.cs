#nullable disable
using System;

namespace NexNet.Internals.Pipelines.Internal;

/// <summary>
/// Throw helpers kept out of line from <see cref="Buffers.BufferWriter"/>'s inlined paths.
/// </summary>
internal static class Throw
{
    internal static void ObjectDisposed(string objectName)
        => throw new ObjectDisposedException(objectName);

    internal static void InvalidOperation(string message = null)
    {
        if (string.IsNullOrWhiteSpace(message)) throw new InvalidOperationException();
        else throw new InvalidOperationException(message);
    }

    internal static void ArgumentOutOfRange(string paramName)
        => throw new ArgumentOutOfRangeException(paramName);
}
