using System;

namespace NexNet.Pipes.NexusStream;

/// <summary>
/// Exception thrown when a stream operation fails.
/// </summary>
public class NexusStreamException : Exception
{
    /// <summary>
    /// Gets the error code that caused the exception.
    /// </summary>
    public StreamErrorCode ErrorCode { get; }

    /// <summary>
    /// Gets whether this is a protocol error that requires disconnection.
    /// </summary>
    public bool IsProtocolError { get; }

    /// <summary>
    /// Creates a new stream exception.
    /// </summary>
    /// <param name="errorCode">The error code.</param>
    /// <param name="message">The error message.</param>
    /// <param name="isProtocolError">Whether this is a protocol error.</param>
    public NexusStreamException(StreamErrorCode errorCode, string message, bool isProtocolError = false)
        : base(message)
    {
        ErrorCode = errorCode;
        IsProtocolError = isProtocolError;
    }

    /// <summary>
    /// Creates a new stream exception with an inner exception.
    /// </summary>
    /// <param name="errorCode">The error code.</param>
    /// <param name="message">The error message.</param>
    /// <param name="innerException">The inner exception.</param>
    /// <param name="isProtocolError">Whether this is a protocol error.</param>
    public NexusStreamException(StreamErrorCode errorCode, string message, Exception innerException, bool isProtocolError = false)
        : base(message, innerException)
    {
        ErrorCode = errorCode;
        IsProtocolError = isProtocolError;
    }

    /// <inheritdoc />
    public override string ToString()
    {
        return $"NexusStreamException: [{ErrorCode}] {Message}{(IsProtocolError ? " (Protocol Error)" : "")}";
    }
}
