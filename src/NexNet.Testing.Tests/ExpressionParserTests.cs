using System;
using System.Linq.Expressions;
using NexNet.Testing.Recording;
using NUnit.Framework;
#pragma warning disable VSTHRD200

namespace NexNet.Testing.Tests;

internal class ExpressionParserTests
{
    public interface ISample
    {
        void Zero();
        void One(int id);
        void Two(int id, string name);
        void Mixed(int id, string name, bool flag);
        int IntValue { get; }
    }

    [Test]
    public void ParsesNoArgMethod()
    {
        Expression<Action<ISample>> expr = n => n.Zero();
        var (method, matchers) = ExpressionParser.Parse(expr);
        Assert.That(method.Name, Is.EqualTo("Zero"));
        Assert.That(matchers, Is.Empty);
    }

    [Test]
    public void ParsesConstantArg()
    {
        Expression<Action<ISample>> expr = n => n.One(42);
        var (_, matchers) = ExpressionParser.Parse(expr);
        Assert.That(matchers, Has.Count.EqualTo(1));
        Assert.That(matchers[0].Matches(42), Is.True);
        Assert.That(matchers[0].Matches(43), Is.False);
    }

    [Test]
    public void ParsesWildcardArg()
    {
        Expression<Action<ISample>> expr = n => n.One(Arg.Any<int>());
        var (_, matchers) = ExpressionParser.Parse(expr);
        Assert.That(matchers, Has.Count.EqualTo(1));
        Assert.That(matchers[0].Matches(0), Is.True);
        Assert.That(matchers[0].Matches(int.MaxValue), Is.True);
        Assert.That(matchers[0].ToString(), Does.Contain("Any"));
    }

    [Test]
    public void ParsesPredicateArg()
    {
        Expression<Action<ISample>> expr = n => n.One(Arg.Is<int>(x => x > 100));
        var (_, matchers) = ExpressionParser.Parse(expr);
        Assert.That(matchers[0].Matches(150), Is.True);
        Assert.That(matchers[0].Matches(50), Is.False);
    }

    [Test]
    public void ParsesMixedArgs()
    {
        Expression<Action<ISample>> expr = n =>
            n.Mixed(7, Arg.Any<string>(), Arg.Is<bool>(b => b));

        var (method, matchers) = ExpressionParser.Parse(expr);
        Assert.That(method.Name, Is.EqualTo("Mixed"));
        Assert.That(matchers, Has.Count.EqualTo(3));

        Assert.That(matchers[0].Matches(7), Is.True);
        Assert.That(matchers[0].Matches(8), Is.False);

        Assert.That(matchers[1].Matches("anything"), Is.True);
        Assert.That(matchers[1].Matches(null), Is.True);

        Assert.That(matchers[2].Matches(true), Is.True);
        Assert.That(matchers[2].Matches(false), Is.False);
    }

    [Test]
    public void ParsesCapturedLocalAsEquality()
    {
        int expected = 99;
        Expression<Action<ISample>> expr = n => n.One(expected);

        var (_, matchers) = ExpressionParser.Parse(expr);
        Assert.That(matchers[0].Matches(99), Is.True);
        Assert.That(matchers[0].Matches(100), Is.False);
    }

    [Test]
    public void StringEqualityMatcher()
    {
        Expression<Action<ISample>> expr = n => n.Two(1, "hello");
        var (_, matchers) = ExpressionParser.Parse(expr);
        Assert.That(matchers[1].Matches("hello"), Is.True);
        Assert.That(matchers[1].Matches("HELLO"), Is.False);
        Assert.That(matchers[1].Matches(null), Is.False);
    }

    [Test]
    public void NonCallExpression_PropertyAccess_Throws()
    {
        // Real misuse: user wrote `n => n.IntValue` instead of `n => n.Method()`. The parser
        // must surface this as a clear argument exception rather than producing an
        // ill-formed matcher.
        Expression<Func<ISample, int>> propertyAccess = n => n.IntValue;
        // Coerce into the Action<ISample> shape the parser accepts so it sees the property-access
        // body, not the wrapper lambda's shape.
        var asAction = Expression.Lambda<Action<ISample>>(
            propertyAccess.Body,
            propertyAccess.Parameters);

        Assert.Throws<ArgumentException>(() => ExpressionParser.Parse(asAction));
    }
}
