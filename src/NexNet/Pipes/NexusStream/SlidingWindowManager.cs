using System;
using System.Threading;
using System.Threading.Tasks;

namespace NexNet.Pipes.NexusStream;

/// <summary>
/// Manages sliding window flow control for data transfer.
/// Provides fine-grained backpressure on top of pipe water marks.
/// </summary>
internal sealed class SlidingWindowManager
{
    /// <summary>
    /// Default window size in bytes (64 KB).
    /// </summary>
    public const uint DefaultWindowSize = 65536;

    /// <summary>
    /// Minimum window size in bytes.
    /// </summary>
    public const uint MinWindowSize = 1024;

    private readonly SemaphoreSlim _windowLock = new(1, 1);
    private readonly SemaphoreSlim _waitForAck = new(0, int.MaxValue);

    private uint _windowSize;
    private uint _lastAcked;
    private uint _lastSent;
    private long _bytesInFlight;
    private bool _disposed;

    /// <summary>
    /// Creates a new sliding window manager with the specified window size.
    /// </summary>
    /// <param name="windowSize">The initial window size in bytes.</param>
    public SlidingWindowManager(uint windowSize = DefaultWindowSize)
    {
        if (windowSize < MinWindowSize)
            throw new ArgumentOutOfRangeException(nameof(windowSize), $"Window size must be at least {MinWindowSize} bytes.");

        _windowSize = windowSize;
        _lastAcked = 0;
        _lastSent = 0;
        _bytesInFlight = 0;
    }

    /// <summary>
    /// Gets the current window size.
    /// </summary>
    public uint WindowSize => _windowSize;

    /// <summary>
    /// Gets the last acknowledged sequence number.
    /// </summary>
    public uint LastAcked => _lastAcked;

    /// <summary>
    /// Gets the last sent sequence number.
    /// </summary>
    public uint LastSent => _lastSent;

    /// <summary>
    /// Gets the number of bytes currently in flight (sent but not acknowledged).
    /// </summary>
    public long BytesInFlight => Interlocked.Read(ref _bytesInFlight);

    /// <summary>
    /// Gets whether the window has available capacity for sending.
    /// </summary>
    public bool CanSend => BytesInFlight < _windowSize;

    /// <summary>
    /// Gets the available window capacity in bytes.
    /// </summary>
    public long AvailableWindow => Math.Max(0, _windowSize - BytesInFlight);

    /// <summary>
    /// Records that data has been sent and updates the in-flight count.
    /// </summary>
    /// <param name="bytesSent">The number of bytes sent.</param>
    /// <param name="sequence">The sequence number assigned to this send.</param>
    /// <returns>True if the send was within window limits, false if it exceeded the window.</returns>
    public bool RecordSend(int bytesSent, out uint sequence)
    {
        sequence = Interlocked.Increment(ref _lastSent);
        var newInFlight = Interlocked.Add(ref _bytesInFlight, bytesSent);

        // Allow send even if slightly over window - caller should have checked CanSend first
        return newInFlight <= _windowSize * 2; // Allow 2x window for buffering
    }

    /// <summary>
    /// Waits until there is window capacity available for sending.
    /// </summary>
    /// <param name="bytesToSend">The number of bytes to send.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if capacity is available, false if waiting was cancelled.</returns>
    public async ValueTask<bool> WaitForWindowAsync(int bytesToSend, CancellationToken ct = default)
    {
        while (!ct.IsCancellationRequested && !_disposed)
        {
            if (AvailableWindow >= bytesToSend)
                return true;

            // Wait for an ack to arrive
            try
            {
                await _waitForAck.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        return false;
    }

    /// <summary>
    /// Processes an acknowledgment, updating the window and releasing waiters.
    /// </summary>
    /// <param name="ackedSequence">The sequence number being acknowledged.</param>
    /// <param name="newWindowSize">The new window size from the receiver.</param>
    /// <param name="bytesAcknowledged">The number of bytes being acknowledged.</param>
    public void OnAck(uint ackedSequence, uint newWindowSize, int bytesAcknowledged)
    {
        // Update window size
        _windowSize = Math.Max(newWindowSize, MinWindowSize);

        // Update last acked (handle wraparound)
        if (IsSequenceGreater(ackedSequence, _lastAcked))
        {
            _lastAcked = ackedSequence;
        }

        // Reduce bytes in flight
        Interlocked.Add(ref _bytesInFlight, -bytesAcknowledged);
        if (Interlocked.Read(ref _bytesInFlight) < 0)
        {
            Interlocked.Exchange(ref _bytesInFlight, 0);
        }

        // Signal waiters
        try
        {
            _waitForAck.Release();
        }
        catch (SemaphoreFullException)
        {
            // Ignore if no one is waiting
        }
    }

    /// <summary>
    /// Resets the sliding window state.
    /// </summary>
    public void Reset()
    {
        _lastAcked = 0;
        _lastSent = 0;
        Interlocked.Exchange(ref _bytesInFlight, 0);
    }

    /// <summary>
    /// Determines if sequence a is greater than sequence b, handling wraparound.
    /// </summary>
    private static bool IsSequenceGreater(uint a, uint b)
    {
        // Handle 32-bit wraparound using signed comparison
        return (int)(a - b) > 0;
    }

    /// <summary>
    /// Disposes the sliding window manager.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        // Release any waiters
        try
        {
            _waitForAck.Release(int.MaxValue);
        }
        catch
        {
            // Ignore
        }

        _windowLock.Dispose();
        _waitForAck.Dispose();
    }
}
