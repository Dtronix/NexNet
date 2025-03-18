using System;
using NexNet.Pipes;

namespace NexNet;

/// <summary>
/// Result of the connection attempt.
/// </summary>
public class ConnectionResult
{
    /// <summary>
    /// State of the connection.
    /// </summary>
    public StateValue State { get; } = StateValue.Unset;
    
    /// <summary>
    /// Exception that occurred while connecting.  Null if no exception occurred.
    /// </summary>
    public Exception? Exception { get; }

    public ConnectionResult(StateValue state, Exception? exception = null)
    {
        State = state;
        Exception = exception;
    }

    /// <summary>
    /// Returns true if the connection was successful.  False otherwise.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"></exception>
    public bool Success => State switch
    {
        StateValue.Success => true,
        StateValue.Timeout => false,
        StateValue.AuthenticationFailed => false,
        StateValue.Exception => false,
        StateValue.Unset => false,
        StateValue.UnknownException => false,
        _ => throw new ArgumentOutOfRangeException()
    };
    
    /// <summary>
    /// Result of a connection attempt.
    /// </summary>
    public enum StateValue
    {
        /// <summary>
        /// State is unset.
        /// </summary>
        Unset,
        
        /// <summary>
        /// Connection was successful.
        /// </summary>
        Success,

        /// <summary>
        /// Connection failed due to a timeout.
        /// </summary>
        Timeout,

        /// <summary>
        /// Connection failed due to an authentication failure.
        /// </summary>
        AuthenticationFailed,

        /// <summary>
        /// Connection failed due to a known exception.
        /// </summary>
        Exception,
        
        /// <summary>
        /// Connection failed due to an unknown exception.
        /// </summary>
        UnknownException
    }
}
