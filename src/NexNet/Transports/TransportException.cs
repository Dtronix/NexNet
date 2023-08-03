using System;

namespace NexNet.Transports;

/// <summary>
/// Exception for transport errors. Used to abstract away the underlying transport.
/// </summary>
public class TransportException : Exception
{
    /// <summary>
    /// Error code
    /// </summary>
    public TransportError Error { get; }

    /// <summary>
    /// Exception for transport errors. Used to abstract away the underlying transport.
    /// </summary>
    /// <param name="error">Error code.</param>
    /// <param name="message">Error description.</param>
    /// <param name="innerException">Source exception.</param>
    public TransportException(TransportError error, string message, Exception innerException)
        : base(message, innerException)
    {
        Error = error;
    }
}
