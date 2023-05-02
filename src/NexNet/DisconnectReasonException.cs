using System;
using NexNet.Messages;

namespace NexNet;

public class DisconnectReasonException : Exception
{
    public DisconnectReason Reason { get; set; }

    public DisconnectReasonException()
    {
            
    }

    public DisconnectReasonException(DisconnectReason reason, Exception? exception)
        :base(null, exception)
    {
        Reason = reason;
    }
}
