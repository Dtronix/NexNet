using System;
using System.Runtime.CompilerServices;
using MemoryPack;
using NexNet.Internals;

namespace NexNet.Messages;

[MemoryPackable]
internal partial class DuplexPipeUpdateStateMessage : IMessageBase
{
    public static MessageType Type { get; } = MessageType.DuplexPipeUpdateState;

    public ushort PipeId { get; set; }

    public NexusDuplexPipeSlim.State State { get; set; }
    
}
