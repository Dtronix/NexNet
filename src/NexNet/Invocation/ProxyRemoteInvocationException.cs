using System;

namespace NexNet.Invocation;

public class ProxyRemoteInvocationException : Exception
{

    public ProxyRemoteInvocationException()
        : base("Exception occurred while executing method on proxy.")
    {
        
    }
}
