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
}
