using System;

namespace NexNet.Pipes.NexusStream;

/// <summary>
/// Manages sequence numbers for data frame validation.
/// Sequence numbers are continuous across the stream's lifetime.
/// </summary>
internal sealed class SequenceManager
{
    private uint _nextExpected;
    private uint _nextToSend;

    /// <summary>
    /// Gets the next sequence number expected to be received.
    /// </summary>
    public uint NextExpected => _nextExpected;

    /// <summary>
    /// Gets the next sequence number to send.
    /// </summary>
    public uint NextToSend => _nextToSend;

    /// <summary>
    /// Gets the next sequence number for sending and increments the counter.
    /// </summary>
    /// <returns>The sequence number to use.</returns>
    public uint GetNextSendSequence()
    {
        return _nextToSend++;
    }

    /// <summary>
    /// Validates a received sequence number.
    /// </summary>
    /// <param name="sequence">The received sequence number.</param>
    /// <returns>True if the sequence is valid (matches expected), false otherwise.</returns>
    public bool ValidateReceived(uint sequence)
    {
        if (sequence != _nextExpected)
        {
            return false;
        }

        _nextExpected++;
        return true;
    }

    /// <summary>
    /// Validates a received sequence number and throws if invalid.
    /// </summary>
    /// <param name="sequence">The received sequence number.</param>
    /// <exception cref="InvalidOperationException">Thrown if the sequence is invalid.</exception>
    public void ValidateReceivedOrThrow(uint sequence)
    {
        var expected = _nextExpected;
        if (!ValidateReceived(sequence))
        {
            throw new InvalidOperationException(
                $"Invalid sequence number: expected {expected}, got {sequence}. " +
                "This indicates a protocol error or data corruption.");
        }
    }

    /// <summary>
    /// Peeks at the next sequence number for sending without incrementing.
    /// </summary>
    /// <returns>The next sequence number that would be used.</returns>
    public uint PeekNextSendSequence()
    {
        return _nextToSend;
    }

    /// <summary>
    /// Resets the sequence manager to initial state.
    /// </summary>
    public void Reset()
    {
        _nextExpected = 0;
        _nextToSend = 0;
    }
}
