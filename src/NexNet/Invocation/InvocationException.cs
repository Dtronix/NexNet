using System;

namespace NexNet.Invocation;

internal class InvocationException : Exception
{
    public InvocationException(int invocationId)
        : base($"Exception occurred on invocation {invocationId}")
    {

    }
}
