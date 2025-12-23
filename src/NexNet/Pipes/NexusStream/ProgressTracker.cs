using System;
using System.Diagnostics;

namespace NexNet.Pipes.NexusStream;

/// <summary>
/// Tracks when progress notifications should be emitted based on byte thresholds
/// and time intervals.
/// </summary>
internal sealed class ProgressTracker
{
    /// <summary>
    /// Default byte threshold for progress notifications (1 MB).
    /// </summary>
    public const long DefaultByteThreshold = 1024 * 1024;

    /// <summary>
    /// Default time interval for progress notifications (5 seconds).
    /// </summary>
    public static readonly TimeSpan DefaultTimeInterval = TimeSpan.FromSeconds(5);

    private readonly long _byteThreshold;
    private readonly TimeSpan _timeInterval;
    private readonly Stopwatch _elapsed;

    private long _lastReportedReadBytes;
    private long _lastReportedWriteBytes;
    private TimeSpan _lastReportedTime;
    private TransferState _lastReportedState;
    private bool _forceNextReport;

    /// <summary>
    /// Creates a new progress tracker with the specified thresholds.
    /// </summary>
    /// <param name="byteThreshold">Minimum bytes transferred before reporting. Default: 1 MB.</param>
    /// <param name="timeInterval">Minimum time between reports. Default: 5 seconds.</param>
    public ProgressTracker(long byteThreshold = DefaultByteThreshold, TimeSpan? timeInterval = null)
    {
        _byteThreshold = byteThreshold;
        _timeInterval = timeInterval ?? DefaultTimeInterval;
        _elapsed = new Stopwatch();
        _lastReportedState = TransferState.Active;
    }

    /// <summary>
    /// Gets the elapsed time since the tracker was started.
    /// </summary>
    public TimeSpan Elapsed => _elapsed.Elapsed;

    /// <summary>
    /// Starts or restarts the elapsed time tracking.
    /// </summary>
    public void Start()
    {
        _elapsed.Restart();
        _lastReportedTime = TimeSpan.Zero;
        _lastReportedReadBytes = 0;
        _lastReportedWriteBytes = 0;
        _lastReportedState = TransferState.Active;
    }

    /// <summary>
    /// Stops the elapsed time tracking.
    /// </summary>
    public void Stop()
    {
        _elapsed.Stop();
    }

    /// <summary>
    /// Determines whether a progress report should be emitted.
    /// </summary>
    /// <param name="currentReadBytes">Current total bytes read.</param>
    /// <param name="currentWriteBytes">Current total bytes written.</param>
    /// <param name="state">Current transfer state.</param>
    /// <returns>True if a progress report should be emitted.</returns>
    public bool ShouldReport(long currentReadBytes, long currentWriteBytes, TransferState state)
    {
        // Check for forced report
        if (_forceNextReport)
        {
            _forceNextReport = false;
            UpdateLastReported(currentReadBytes, currentWriteBytes, state);
            return true;
        }

        // Always report on state change
        if (state != _lastReportedState)
        {
            UpdateLastReported(currentReadBytes, currentWriteBytes, state);
            return true;
        }

        // Always report on completion or failure
        if (state == TransferState.Complete || state == TransferState.Failed)
        {
            UpdateLastReported(currentReadBytes, currentWriteBytes, state);
            return true;
        }

        var currentTime = _elapsed.Elapsed;
        var timeSinceLastReport = currentTime - _lastReportedTime;
        var readBytesSinceLastReport = currentReadBytes - _lastReportedReadBytes;
        var writeBytesSinceLastReport = currentWriteBytes - _lastReportedWriteBytes;
        var totalBytesSinceLastReport = readBytesSinceLastReport + writeBytesSinceLastReport;

        // Report if byte threshold exceeded
        if (totalBytesSinceLastReport >= _byteThreshold)
        {
            UpdateLastReported(currentReadBytes, currentWriteBytes, state);
            return true;
        }

        // Report if time interval exceeded
        if (timeSinceLastReport >= _timeInterval)
        {
            UpdateLastReported(currentReadBytes, currentWriteBytes, state);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Calculates the transfer rate in bytes per second.
    /// </summary>
    /// <param name="bytesTransferred">Total bytes transferred.</param>
    /// <returns>Bytes per second, or 0 if no time has elapsed.</returns>
    public double CalculateRate(long bytesTransferred)
    {
        var seconds = _elapsed.Elapsed.TotalSeconds;
        return seconds > 0 ? bytesTransferred / seconds : 0;
    }

    /// <summary>
    /// Forces the next ShouldReport call to return true (for state changes).
    /// </summary>
    public void ForceNextReport()
    {
        _forceNextReport = true;
    }

    private void UpdateLastReported(long readBytes, long writeBytes, TransferState state)
    {
        _lastReportedReadBytes = readBytes;
        _lastReportedWriteBytes = writeBytes;
        _lastReportedTime = _elapsed.Elapsed;
        _lastReportedState = state;
    }
}
