namespace NexNet.Pipes.NexusStream;

/// <summary>
/// Error codes for stream operations.
/// Codes 1-99 are recoverable stream errors (session remains connected).
/// Codes 100+ are protocol errors (session disconnects).
/// </summary>
public enum StreamErrorCode : int
{
    /// <summary>
    /// No error - operation succeeded.
    /// </summary>
    Success = 0,

    // =============================================
    // Stream Errors (1-99) - Recoverable
    // =============================================

    /// <summary>
    /// The requested resource does not exist.
    /// </summary>
    FileNotFound = 1,

    /// <summary>
    /// Permission to access the resource was denied.
    /// </summary>
    AccessDenied = 2,

    /// <summary>
    /// The resource is locked by another process.
    /// </summary>
    SharingViolation = 3,

    /// <summary>
    /// Storage capacity has been exceeded.
    /// </summary>
    DiskFull = 4,

    /// <summary>
    /// A general I/O failure occurred.
    /// </summary>
    IoError = 5,

    /// <summary>
    /// The operation is not valid for the current stream state.
    /// </summary>
    InvalidOperation = 6,

    /// <summary>
    /// The operation timed out.
    /// </summary>
    Timeout = 7,

    /// <summary>
    /// The operation was cancelled.
    /// </summary>
    Cancelled = 8,

    /// <summary>
    /// Unexpected end of stream encountered.
    /// </summary>
    EndOfStream = 9,

    /// <summary>
    /// The seek operation failed.
    /// </summary>
    SeekError = 10,

    // =============================================
    // Protocol Errors (100+) - Session Disconnects
    // =============================================

    /// <summary>
    /// An unknown frame type was received.
    /// </summary>
    InvalidFrameType = 100,

    /// <summary>
    /// Frames were received in an invalid order.
    /// </summary>
    InvalidFrameSequence = 101,

    /// <summary>
    /// The frame structure is invalid or corrupted.
    /// </summary>
    MalformedFrame = 102,

    /// <summary>
    /// A gap was detected in data frame sequence numbers.
    /// </summary>
    SequenceGap = 103,

    /// <summary>
    /// A frame was received that is not valid for the current state.
    /// </summary>
    UnexpectedFrame = 104
}
