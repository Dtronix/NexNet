using System;

namespace NexNet.Pipes.NexusStream;

/// <summary>
/// Specifies the requested access mode for a stream.
/// </summary>
[Flags]
public enum StreamAccessMode : byte
{
    /// <summary>
    /// Read access requested.
    /// </summary>
    Read = 0x01,

    /// <summary>
    /// Write access requested.
    /// </summary>
    Write = 0x02,

    /// <summary>
    /// Both read and write access requested.
    /// </summary>
    ReadWrite = Read | Write
}
