using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;

namespace NexNet.Testing.Recording;

/// <summary>
/// Resolves an assertion lambda like <c>n =&gt; n.M(42, Arg.Any&lt;string&gt;())</c> into a
/// callable contract: the target <see cref="MethodInfo"/> on the interface and a list of
/// <see cref="ArgMatcher"/> values, one per parameter.
/// </summary>
internal static class ExpressionParser
{
    private static readonly MethodInfo ArgAnyOpenGeneric = typeof(Arg).GetMethod(
        nameof(Arg.Any),
        BindingFlags.Public | BindingFlags.Static)!;

    private static readonly MethodInfo ArgIsOpenGeneric = typeof(Arg).GetMethod(
        nameof(Arg.Is),
        BindingFlags.Public | BindingFlags.Static)!;

    public static (MethodInfo Method, IReadOnlyList<ArgMatcher> Matchers) Parse<TInterface>(
        Expression<Action<TInterface>> expression)
    {
        if (expression.Body is not MethodCallExpression call)
            throw new ArgumentException(
                "Assertion expression must be a single method call on the interface, " +
                $"got: {expression.Body.NodeType}",
                nameof(expression));

        var parameters = call.Method.GetParameters();
        var matchers = new ArgMatcher[parameters.Length];

        for (int i = 0; i < call.Arguments.Count; i++)
        {
            matchers[i] = ResolveArgument(call.Arguments[i], parameters[i].ParameterType);
        }

        return (call.Method, matchers);
    }

    private static ArgMatcher ResolveArgument(Expression argExpr, Type parameterType)
    {
        // Strip any cast / convert nodes the compiler inserted for boxing or co/contra-variance.
        while (argExpr is UnaryExpression u
               && (u.NodeType is ExpressionType.Convert or ExpressionType.ConvertChecked))
        {
            argExpr = u.Operand;
        }

        if (argExpr is MethodCallExpression mc)
        {
            var def = mc.Method.IsGenericMethod ? mc.Method.GetGenericMethodDefinition() : mc.Method;

            if (def == ArgAnyOpenGeneric)
            {
                return ArgMatcher.Wildcard(parameterType);
            }

            if (def == ArgIsOpenGeneric)
            {
                // The predicate sits inside the call's first argument as a quoted lambda.
                if (mc.Arguments.Count != 1)
                    throw new ArgumentException("Arg.Is must take a single predicate argument.");

                var predicateExpr = mc.Arguments[0];
                // Compile to delegate. Captured locals are honored automatically.
                var compiled = Expression.Lambda(predicateExpr).Compile().DynamicInvoke();
                if (compiled is not Delegate del)
                    throw new ArgumentException("Arg.Is predicate did not resolve to a delegate.");
                return ArgMatcher.Predicate(del, parameterType);
            }
        }

        // Anything else (constants, captured locals, expressions) is reduced to a value via
        // Expression.Lambda(...).Compile() and matched with equality.
        var value = Expression.Lambda(argExpr).Compile().DynamicInvoke();
        return ArgMatcher.Equality(value);
    }
}
