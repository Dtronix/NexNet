using System;
using System.Threading.Tasks;
using NexNet.Messages;

namespace NexNet.Internals;

/// <summary>
/// Optional internal hook that wraps the dispatch of an incoming invocation. When installed via
/// <see cref="NexNet.Transports.ConfigBase.InvocationInterceptor"/>, the session calls
/// <see cref="WrapAsync"/> in place of invoking the nexus method directly, allowing the caller
/// (e.g., the test harness) to record the invocation and track in-flight dispatch counts for
/// quiescence purposes.
/// </summary>
/// <remarks>
/// Outer-only interception: the implementation sees the raw <see cref="InvocationMessage"/>
/// (method id + serialized argument bytes), not deserialized arguments. To preserve normal
/// dispatch semantics, the implementation MUST call the supplied invoke delegate exactly once
/// (typically inside a try/finally so counter state stays balanced if the invocation throws).
/// </remarks>
internal interface IInvocationInterceptor
{
    /// <summary>
    /// Wraps the dispatch of a single incoming invocation. Implementations must invoke
    /// <paramref name="invoke"/> to run the underlying nexus method.
    /// </summary>
    /// <param name="message">The raw invocation message about to be dispatched.</param>
    /// <param name="invoke">Delegate that runs the underlying nexus method dispatch.</param>
    ValueTask WrapAsync(InvocationMessage message, Func<ValueTask> invoke);
}
