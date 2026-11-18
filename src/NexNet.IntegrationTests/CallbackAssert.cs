using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace NexNet.IntegrationTests;

/// <summary>
/// Synchronous asserts for callbacks that cannot await a TUnit assert (sync lambdas, nexus event handlers, transport
/// hooks). A failure throws through <see cref="Assert.Fail(string)"/>, as the NUnit asserts these replace did, so a
/// failure inside a callback whose exceptions are swallowed still shows up as the test's timeout or as a missing result.
/// Prefer capturing the value and asserting after the await when the callback's result is awaited anyway.
/// </summary>
internal static class CallbackAssert
{
    public static void That([DoesNotReturnIf(false)] bool condition,
        [CallerArgumentExpression(nameof(condition))] string? expression = null)
    {
        if (!condition)
            Assert.Fail($"Expected {expression} to be true.");
    }

    /// <summary>
    /// Equality; sequences (other than strings) compare element by element, as NUnit's <c>Is.EqualTo</c> did.
    /// </summary>
    public static void Equal<T>(T actual, T expected,
        [CallerArgumentExpression(nameof(actual))] string? expression = null)
    {
        if (actual is IEnumerable actualItems and not string && expected is IEnumerable expectedItems and not string)
        {
            if (!actualItems.Cast<object?>().SequenceEqual(expectedItems.Cast<object?>()))
                Assert.Fail($"Expected {expression} to have the same items in the same order.");
            return;
        }

        if (!EqualityComparer<T>.Default.Equals(actual, expected))
            Assert.Fail($"Expected {expression} to be {expected} but was {actual}.");
    }

    public static void NotNull([NotNull] object? value,
        [CallerArgumentExpression(nameof(value))] string? expression = null)
    {
        if (value is null)
            throw Fail($"Expected {expression} to be non-null.");
    }

    private static Exception Fail(string message)
    {
        Assert.Fail(message);
        return new InvalidOperationException(message); // Not reached: Assert.Fail throws.
    }

    public static void Null(object? value,
        [CallerArgumentExpression(nameof(value))] string? expression = null)
    {
        if (value is not null)
            Assert.Fail($"Expected {expression} to be null but was {value}.");
    }
}
