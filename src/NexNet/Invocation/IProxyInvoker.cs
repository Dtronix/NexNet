using NexNet.Internals;

namespace NexNet.Invocation;

/// <summary>
/// Interface for invocations on on remote hubs.
/// </summary>
public interface IProxyInvoker
{
    internal void Configure(
        INexNetSession? session,
        SessionManager sessionManager,
        ProxyInvocationMode mode,
        object? modeArguments);
}
