using System;

namespace NexNet;

/// <summary>
/// Exception that occurs when a client connection fails. 
/// </summary>
public class ClientPoolConnectionException : Exception
{
    /// <summary>
    /// Result of the connection
    /// </summary>
    public ConnectionResult ConnectionResult { get; }

    /// <summary>
    /// Sets the exception to the passed result.
    /// </summary>
    /// <param name="message">Message of the exception</param>
    /// <param name="connectionResult"></param>
    public ClientPoolConnectionException(string message, ConnectionResult connectionResult)
        : base(message, connectionResult.Exception)
    {
        ConnectionResult = connectionResult;
    }
}
