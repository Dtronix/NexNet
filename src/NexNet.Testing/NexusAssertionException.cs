using System;

namespace NexNet.Testing;

/// <summary>
/// Thrown by <c>AssertReceived</c> / <c>AssertNotReceived</c> when an expected invocation is
/// missing, an unexpected one is observed, or the call-count assertion fails. NUnit, xUnit,
/// and MSTest each surface uncaught exceptions as test failures, so the harness needs no
/// framework-specific integration.
/// </summary>
public sealed class NexusAssertionException : Exception
{
    /// <summary>
    /// Creates a new <see cref="NexusAssertionException"/> with the supplied diagnostic message.
    /// </summary>
    public NexusAssertionException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Creates a new <see cref="NexusAssertionException"/> wrapping an inner exception, e.g.
    /// when a user-supplied predicate inside <c>Arg.Is&lt;T&gt;(p)</c> throws and the harness
    /// wants to surface the original cause rather than masking it as a no-match.
    /// </summary>
    public NexusAssertionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
