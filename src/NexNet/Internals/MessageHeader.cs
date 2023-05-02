using NexNet.Messages;

namespace NexNet.Internals;

internal struct MessageHeader
{
    public ushort BodyLength = ushort.MaxValue;
    public MessageType Type = MessageType.Unset;

    public MessageHeader()
    {
    }

    public void Reset()
    {
        BodyLength = ushort.MaxValue;
        Type = MessageType.Unset;
    }
}
