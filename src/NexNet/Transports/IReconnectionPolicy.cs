using System;

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
}
