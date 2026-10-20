using System;

namespace NexNet.Pipes.NexusStream;

/// <summary>
/// Specifies the sharing mode for a stream resource.
/// </summary>
[Flags]
public enum StreamShareMode : byte
{
    /// <summary>
    /// Exclusive access - no concurrent access allowed.
    /// </summary>
    None = 0x00,

    /// <summary>
    /// Allow concurrent readers.
    /// </summary>
    Read = 0x01,

    /// <summary>
    /// Allow concurrent writers.
    /// </summary>
    Write = 0x02,

    /// <summary>
    /// Allow concurrent readers and writers.
    /// </summary>
    ReadWrite = Read | Write
}
