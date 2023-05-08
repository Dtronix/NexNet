using System;

namespace NexNet.Messages;

internal interface IMessageBodyBase
{
    public static abstract MessageType Type { get; }

}
