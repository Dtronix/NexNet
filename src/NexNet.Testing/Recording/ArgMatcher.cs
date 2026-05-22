using System;
using System.Collections.Generic;

namespace NexNet.Testing.Recording;

/// <summary>
/// One argument's matcher. Constructed by <see cref="ExpressionParser"/> from an argument
/// expression inside an assertion lambda; tested against a deserialized recorded value at
/// assertion time.
/// </summary>
internal abstract class ArgMatcher
{
    /// <summary>Indicates whether <paramref name="actual"/> satisfies this matcher.</summary>
    public abstract bool Matches(object? actual);

    /// <summary>Human-readable rendering used in <see cref="NexusAssertionException"/> messages.</summary>
    public abstract override string ToString();

    public static ArgMatcher Wildcard(Type type) => new WildcardMatcher(type);
    public static ArgMatcher Equality(object? expected) => new EqualityMatcher(expected);
    public static ArgMatcher Predicate(Delegate predicate, Type argType) => new PredicateMatcher(predicate, argType);

    private sealed class WildcardMatcher : ArgMatcher
    {
        private readonly Type _type;
        public WildcardMatcher(Type type) => _type = type;
        public override bool Matches(object? actual) => true;
        public override string ToString() => $"Arg.Any<{_type.Name}>()";
    }

    private sealed class EqualityMatcher : ArgMatcher
    {
        private readonly object? _expected;
        public EqualityMatcher(object? expected) => _expected = expected;

        public override bool Matches(object? actual)
        {
            if (_expected is null) return actual is null;
            return EqualityComparer<object>.Default.Equals(_expected, actual);
        }

        public override string ToString() => _expected switch
        {
            null => "null",
            string s => $"\"{s}\"",
            _ => _expected.ToString() ?? "null"
        };
    }

    private sealed class PredicateMatcher : ArgMatcher
    {
        private readonly Delegate _predicate;
        private readonly Type _argType;

        public PredicateMatcher(Delegate predicate, Type argType)
        {
            _predicate = predicate;
            _argType = argType;
        }

        public override bool Matches(object? actual)
        {
            // The predicate is typed Func<T, bool>; invoke via DynamicInvoke so we don't need
            // to thread <T> through the recorder. Fast path: actual is the correct type or null
            // for reference types. If the cast fails, the assertion fails cleanly.
            try
            {
                var result = _predicate.DynamicInvoke(actual);
                return result is bool b && b;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public override string ToString() => $"Arg.Is<{_argType.Name}>(predicate)";
    }
}
