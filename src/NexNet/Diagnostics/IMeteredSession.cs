using NexNet.Messages;

namespace NexNet.Diagnostics;

/// <summary>
/// A session counted by <see cref="NexusMetrics"/>.
/// </summary>
internal interface IMeteredSession
{
    /// <summary>
    /// The tags (role and transport) this session is counted under.
    /// </summary>
    MetricsTagSet MetricsTags { get; }

    /// <summary>
    /// Index in <see cref="MetricsSessionTable"/>; -1 when not in it. Only the table reads or writes it, under its
    /// owner's lock.
    /// </summary>
    int MetricsSlot { get; set; }

    /// <summary>
    /// <see cref="System.Diagnostics.Stopwatch"/> timestamp of when the session was added to the table.
    /// </summary>
    long MetricsStartTimestamp { get; set; }

    /// <summary>
    /// True once the session has disconnected; a collection retires such a session if nothing else did.
    /// </summary>
    bool IsDisconnected { get; }

    /// <summary>
    /// Why the session disconnected; <see cref="DisconnectReason.None"/> while connected.
    /// </summary>
    DisconnectReason DisconnectReason { get; }

    /// <summary>
    /// True once the handshake (and, on a server, authentication) has completed.
    /// </summary>
    bool IsEstablished { get; }

    /// <summary>
    /// Adds the session's current values to <paramref name="totals"/>. Called during a collection from a thread other
    /// than the session's: reads only fields that are safe to read without a lock and never blocks.
    /// </summary>
    void CollectMetrics(ref SessionMetricsTotals totals);
}
