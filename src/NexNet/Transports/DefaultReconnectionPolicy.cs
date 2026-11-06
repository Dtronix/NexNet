using System;
using System.Collections.Generic;
using NexNet.Messages;

namespace NexNet.Transports;

/// <summary>
/// Default reconnection policy retries to connect at 0, 2, 10 &amp; 30 seconds.  Continues to try to reconnect every 30 seconds.
/// Reconnects only for the disconnect reasons in <see cref="ReconnectReasons"/>.
/// </summary>
public class DefaultReconnectionPolicy : IReconnectionPolicy
{
    private readonly IReadOnlySet<DisconnectReason> _reconnectReasons = new HashSet<DisconnectReason>
    {
        DisconnectReason.SocketError,
        DisconnectReason.Timeout,
        DisconnectReason.ServerRestarting
    };

    /// <summary>
    /// Disconnect reasons the client reconnects for.  Defaults to <see cref="DisconnectReason.SocketError"/>,
    /// <see cref="DisconnectReason.Timeout"/> and <see cref="DisconnectReason.ServerRestarting"/>.
    /// Add <see cref="DisconnectReason.ServerShutdown"/> to reconnect after the server stops and starts again.
    /// <see cref="DisconnectReason.Graceful"/> and <see cref="DisconnectReason.Authentication"/> are best left out.
    /// </summary>
    public IReadOnlySet<DisconnectReason> ReconnectReasons
    {
        get => _reconnectReasons;
        init => _reconnectReasons = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>
    /// Timespans used for the reconnection times.
    /// </summary>
    public TimeSpan[] TimeSpans { get; }

    /// <summary>
    /// If set to true the last <see cref="TimeSpans"/> will be used to repeat.
    /// </summary>
    public bool ContinuousRetry { get; }

    /// <inheritdoc />
    public event ReconnectionAttemptDelegate? Reconnection;

    /// <summary>
    /// Creates a reconnection in default mode.
    /// </summary>
    public DefaultReconnectionPolicy()
        : this(null)
    {

    }

    /// <summary>
    /// Creates a reconnection policy with the specified timespans.
    /// </summary>
    /// <param name="timeSpans">Timespans to use for reconnection delays.</param>
    /// <param name="continuousRetry">If set to true the last <see cref="TimeSpans"/> will be used to repeat.</param>
    public DefaultReconnectionPolicy(TimeSpan[]? timeSpans, bool continuousRetry = true)
    {
        timeSpans ??= new[]
        {
            TimeSpan.FromSeconds(0),
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(30)
        };

        TimeSpans = timeSpans;
        ContinuousRetry = continuousRetry;
    }

    /// <inheritdoc />
    public TimeSpan? ReconnectDelay(int retryAttempt)
    {
        if (retryAttempt < TimeSpans.Length)
            return TimeSpans[retryAttempt];

        if (ContinuousRetry)
        {
            return TimeSpans[^1];
        }

        return null;
    }

    /// <inheritdoc />
    public bool ShouldReconnect(DisconnectReason reason)
    {
        return ReconnectReasons.Contains(reason);
    }

    /// <inheritdoc />
    void IReconnectionPolicy.FireReconnection(INexusClient client, int retryAttempt)
    {
        Reconnection?.Invoke(client, retryAttempt);
    }
}
