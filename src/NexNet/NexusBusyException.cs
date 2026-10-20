using System;

namespace NexNet;

/// <summary>
/// Thrown when a pipe or value can't be accepted right now because a session limit is reached: the session
/// budget, or the maximum number of pipes. The session stays connected; the operation may succeed later.
/// </summary>
public class NexusBusyException : Exception
{
    /// <summary>
    /// Creates the exception.
    /// </summary>
    /// <param name="message">What was refused and why.</param>
    public NexusBusyException(string message)
        : base(message)
    {
    }
}
