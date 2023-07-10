using System;
using MemoryPack;

namespace NexNet.Messages;

internal interface IMessageBase
{
    public static abstract MessageType Type { get; }
}
