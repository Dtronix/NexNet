﻿using NexNet.Messages;

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

    /// <summary>
    /// Combination of the body post header length + the optional ushort for the body length
    /// </summary>
    public int TotalHeaderLength = -1;

    /// <summary>
    /// True if the header has been read completely.
    /// </summary>
    public bool IsHeaderComplete = false;

    /// <summary>
    /// Invocation Id used for pipes.
    /// </summary>
    public int InvocationId = 0;

    public void SetTotalHeaderSize(int postHeaderSize, bool hasBody)
    {
        PostHeaderLength = postHeaderSize;
        if (hasBody)
        {
            BodyLength = 0;
            TotalHeaderLength = 2 + postHeaderSize;
        }
        else
        {
            TotalHeaderLength = postHeaderSize;

            if(postHeaderSize == 0)
                IsHeaderComplete = true;
        }
    }

    public MessageHeader()
    {
    }

    public void Reset()
    {
        BodyLength = -1;
        Type = MessageType.Unset;
        PostHeaderLength = -1;
        TotalHeaderLength= -1;
        IsHeaderComplete = false;
        InvocationId = 0;
    }
}
