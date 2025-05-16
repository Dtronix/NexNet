using System;
using NexNet.Invocation;

namespace NexNet.Transports;

/// <summary>
/// Interface for reconnection delays.
/// </summary>
public interface IReconnectionPolicy
{

    /// <summary>
    /// Gets the reconnection delay for the retry count.
    /// </summary>
    /// <param name="retryAttempt">Retry attempt number</param>
    /// <returns>Reconnection delay time.  Null if the retries should be canceled.</returns>
    TimeSpan? ReconnectDelay(int retryAttempt);

    /// <summary>
    /// Invoked upon each reconnection attempt. 
    /// </summary>
    event ReconnectionAttemptDelegate Reconnection;

    /// <summary>
    /// Fires the ReconnectionAttempt event.
    /// </summary>
    /// <param name="client">Client this reconnection attempt is originating from.</param>
    /// <param name="retryAttempt">The current retry attempt.</param>
    internal void FireReconnection(INexusClient client, int retryAttempt);
}

/// <summary>
/// Delegate used for invocation upon reconnection attempts.
/// </summary>
public delegate void ReconnectionAttemptDelegate(INexusClient client, int retryAttempt);
