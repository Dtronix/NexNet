namespace NexNet.Invocation;

/// <summary>
/// Exception thrown on the calling side when a method invocation is denied by the server's authorization check.
/// </summary>
public sealed class ProxyUnauthorizedException : ProxyRemoteInvocationException
{
    /// <summary>
    /// Creates a new instance of <see cref="ProxyUnauthorizedException"/>.
    /// </summary>
    public ProxyUnauthorizedException()
        : base("Method invocation was unauthorized by the server.")
    {
    }
}
