using NexNet.Messages;

namespace NexNet.Internals;

internal struct MessageHeader
{
    /// <summary>
    /// Type of message this is.
    /// </summary>
    public MessageType Type = MessageType.Unset;

    /// <summary>
    /// Number of bytes in the body.
    /// </summary>
    public int BodyLength = -1;

    /// <summary>
    /// Number of bytes required to read the post header.
    /// </summary>
    public int PostHeaderLength = -1;

    public MessageHeader()
    {
    }

    public void Reset()
    {
        BodyLength = -1;
        Type = MessageType.Unset;
        PostHeaderLength = -1;
    }
}
