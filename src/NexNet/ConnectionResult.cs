namespace NexNet;

/// <summary>
/// Result of a connection attempt.
/// </summary>
public enum ConnectionResult
{
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
    /// Connection failed due to an unknown exception.
    /// </summary>
    Exception
}
