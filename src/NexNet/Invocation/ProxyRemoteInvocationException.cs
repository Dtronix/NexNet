using System;

namespace NexNet.Invocation;

/// <summary>
/// Exception thrown when a proxy invocation fails in the remote execution process.
/// </summary>
public class ProxyRemoteInvocationException : Exception
{

    /// <summary>
    /// Constructs the exception with a default result
    /// </summary>
    public ProxyRemoteInvocationException()
        : base("Exception occurred while executing method on proxy.")
    {
        
    }
}
