using System;
using System.Linq.Expressions;
using System.Threading.Tasks;
using NexNet.Collections;
using NexNet.Invocation;
using NexNet.Testing.Recording;

namespace NexNet.Testing;

/// <summary>
/// Server-side assertion API exposed on the host. Resolves the user expression to a
/// <see cref="System.Reflection.MethodInfo"/>, looks up the corresponding method id via the
/// <see cref="MethodIdMap"/>, scans the host's recorder, and either succeeds, throws a
/// <see cref="NexusAssertionException"/>, or awaits a match.
/// </summary>
public sealed partial class NexusTestHost<TServerNexus, TClientProxy, TClientNexus, TServerProxy>
    where TServerNexus : ServerNexusBase<TClientProxy>, IInvocationMethodHash, ICollectionConfigurer, new()
    where TClientProxy : ProxyInvocationBase, IInvocationMethodHash, new()
    where TClientNexus : ClientNexusBase<TServerProxy>, IMethodInvoker, IInvocationMethodHash, ICollectionConfigurer, new()
    where TServerProxy : ProxyInvocationBase, IProxyInvoker, IInvocationMethodHash, new()
{
    private RecorderAssertions? _serverAssertions;
    private RecorderAssertions ServerAssertions =>
        _serverAssertions ??= new RecorderAssertions(ServerRecorder);

    /// <summary>
    /// Asserts the server has dispatched a method matching <paramref name="expression"/>
    /// exactly <paramref name="times"/> times. Call after <c>QuiesceAsync</c> so that any
    /// in-flight invocations have been recorded.
    /// </summary>
    public void AssertReceived<TInterface>(Expression<Action<TInterface>> expression, int times = 1)
        => ServerAssertions.AssertReceived(expression, times);

    /// <summary>
    /// Asserts no recorded invocation matches <paramref name="expression"/>. Call after
    /// <c>QuiesceAsync</c>.
    /// </summary>
    public void AssertNotReceived<TInterface>(Expression<Action<TInterface>> expression)
        => ServerAssertions.AssertNotReceived(expression);

    /// <summary>
    /// Awaits an invocation matching <paramref name="expression"/> arriving within
    /// <paramref name="timeout"/> (default 5 seconds). Returns immediately if a match is
    /// already in the recorder; throws <see cref="TimeoutException"/> when the deadline
    /// elapses with no match.
    /// </summary>
    public Task WaitFor<TInterface>(
        Expression<Action<TInterface>> expression,
        TimeSpan? timeout = null)
        => ServerAssertions.WaitFor(expression, timeout);
}
