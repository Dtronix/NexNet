namespace NexNet.Pipes.NexusStream.Frames;

/// <summary>
/// Identifies the type of a NexStream protocol frame.
/// </summary>
public enum FrameType : byte
{
    /// <summary>
    /// Request to open a stream for a resource.
    /// </summary>
    Open = 0x01,

    /// <summary>
    /// Response to an Open request with metadata.
    /// </summary>
    OpenResponse = 0x02,

    /// <summary>
    /// Closes the current stream.
    /// </summary>
    Close = 0x03,

    /// <summary>
    /// Request to change stream position.
    /// </summary>
    Seek = 0x04,

    /// <summary>
    /// Response to a Seek request with new position.
    /// </summary>
    SeekResponse = 0x05,

    /// <summary>
    /// Request to flush buffered data to storage.
    /// </summary>
    Flush = 0x06,

    /// <summary>
    /// Response to a Flush request.
    /// </summary>
    FlushResponse = 0x07,

    /// <summary>
    /// Request for current stream metadata.
    /// </summary>
    GetMetadata = 0x08,

    /// <summary>
    /// Response containing stream metadata.
    /// </summary>
    MetadataResponse = 0x09,

    /// <summary>
    /// Request to read N bytes from the stream.
    /// </summary>
    Read = 0x0A,

    /// <summary>
    /// Request to write data to the stream.
    /// </summary>
    Write = 0x0B,

    /// <summary>
    /// Response acknowledging write completion.
    /// </summary>
    WriteResponse = 0x0C,

    /// <summary>
    /// Request to set the stream length.
    /// </summary>
    SetLength = 0x0D,

    /// <summary>
    /// Response to a SetLength request.
    /// </summary>
    SetLengthResponse = 0x0E,

    /// <summary>
    /// Binary data chunk.
    /// </summary>
    Data = 0x10,

    /// <summary>
    /// Marks the end of a data sequence.
    /// </summary>
    DataEnd = 0x11,

    /// <summary>
    /// Transfer progress notification.
    /// </summary>
    Progress = 0x20,

    /// <summary>
    /// Stream error notification.
    /// </summary>
    Error = 0x30,

    /// <summary>
    /// Sliding window acknowledgment for flow control.
    /// </summary>
    Ack = 0x40
}
