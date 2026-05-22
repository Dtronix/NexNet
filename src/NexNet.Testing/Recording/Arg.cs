using System;

namespace NexNet.Testing;

/// <summary>
/// Sentinel matchers used inside the lambda passed to <c>AssertReceived</c>,
/// <c>AssertNotReceived</c>, and <c>WaitFor</c>. The calls return <c>default(T)</c> at
/// runtime; the harness's <c>ExpressionParser</c> recognizes them by their method handle
/// when walking the lambda body and translates them into wildcard / predicate matchers.
/// </summary>
public static class Arg
{
    /// <summary>
    /// Matches any value of <typeparamref name="T"/>. Use as a placeholder when only some
    /// arguments are interesting: <c>n.M(42, Arg.Any&lt;string&gt;())</c>.
    /// </summary>
    public static T Any<T>() => default!;

    /// <summary>
    /// Matches values of <typeparamref name="T"/> for which <paramref name="predicate"/>
    /// returns true. The predicate is captured by <c>ExpressionParser</c> and invoked at
    /// assertion time against each recorded argument value.
    /// </summary>
    public static T Is<T>(Func<T, bool> predicate)
    {
        _ = predicate; // referenced so callers can pass without warning; consumed via expression tree
        return default!;
    }
}
