#nullable disable
using System.IO.Pipelines;

namespace NexNet.Internals.Pipelines;

/// <summary>
/// A duplex pipe that measures the bytes sent/received
/// </summary>
internal interface IMeasuredDuplexPipe : IDuplexPipe
{
    /// <summary>
    /// The total number of bytes sent to the pipe
    /// </summary>
    long TotalBytesSent { get; }

    /// <summary>
    /// The total number of bytes received by the pipe
    /// </summary>
    long TotalBytesReceived { get; }
}