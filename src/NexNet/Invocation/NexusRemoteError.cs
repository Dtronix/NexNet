namespace NexNet.Invocation;

/// <summary>
/// An application error sent to the caller of a failed invocation in place of the exception's detail. Returned by
/// the nexus's <c>OnInvocationException</c>.
/// </summary>
/// <param name="Code">Application error code, or null for none. Read by the caller as
/// <see cref="ProxyRemoteInvocationException.RemoteErrorCode"/>.</param>
/// <param name="Message">Message for the caller, or null for none; truncated to 2048 characters. Read by the caller
/// as <see cref="ProxyRemoteInvocationException.RemoteMessage"/>.</param>
public sealed record NexusRemoteError(int? Code, string? Message);
