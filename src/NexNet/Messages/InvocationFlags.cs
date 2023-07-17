using System;

namespace NexNet.Messages;

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
    IgnoreReturn = 1 << 0,

    /// <summary>
    /// Informs the invoker that the arguments contain a duplex pipe.
    /// </summary>
    DuplexPipe = 1 << 1
}
