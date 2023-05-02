namespace NexNet.Messages;

public interface IMessageBodyBase
{
    static abstract MessageType Type { get; }
}