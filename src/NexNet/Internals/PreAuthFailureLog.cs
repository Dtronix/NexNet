using System;
using System.Text;
using System.Threading;
using NexNet.Logging;
using NexNet.Messages;

namespace NexNet.Internals;

/// <summary>
/// Counts connections that fail before authentication completes and logs them as one aggregated warning per
/// <see cref="Interval"/>. Hostile peers can cause any number of these, so they are never logged one by one, and
/// nothing the peer sent is part of the message.
/// </summary>
internal sealed class PreAuthFailureLog
{
    /// <summary>
    /// Shortest time between two warnings.
    /// </summary>
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);

    // Index 0 counts transports closed because MaxPendingHandshakes was reached; the others are disconnect reasons.
    private readonly int[] _counts = new int[256];
    private readonly INexusLogger? _logger;
    private readonly TimeProvider _time;
    private readonly Lock _lock = new();
    private long _lastWarning = long.MinValue;

    public PreAuthFailureLog(INexusLogger? logger, TimeProvider time)
    {
        _logger = logger;
        _time = time;
    }

    /// <summary>
    /// Records a session that disconnected before authentication completed.
    /// </summary>
    public void Record(DisconnectReason reason) => RecordCore((byte)reason);

    /// <summary>
    /// Records a transport closed because <c>MaxPendingHandshakes</c> was reached.
    /// </summary>
    public void RecordHandshakeLimit() => RecordCore(0);

    private void RecordCore(byte index)
    {
        if (_logger == null)
            return;

        string? message = null;
        lock (_lock)
        {
            _counts[index]++;
            var now = _time.GetTimestamp();
            if (_lastWarning != long.MinValue && _time.GetElapsedTime(_lastWarning, now) < Interval)
                return;

            _lastWarning = now;
            message = Drain();
        }

        _logger.LogWarning(message);
    }

    private string Drain()
    {
        var builder = new StringBuilder("Connections failed before authentication: ");
        var first = true;
        for (var i = 0; i < _counts.Length; i++)
        {
            if (_counts[i] == 0)
                continue;

            if (!first)
                builder.Append(", ");
            first = false;
            builder.Append(i == 0 ? "MaxPendingHandshakes" : ((DisconnectReason)i).ToString())
                .Append('=')
                .Append(_counts[i]);
            _counts[i] = 0;
        }

        return builder.Append('.').ToString();
    }
}
