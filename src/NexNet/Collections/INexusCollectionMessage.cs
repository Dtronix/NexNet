using System;
using MemoryPack;
using NexNet.Collections.Lists;
using NexNet.Pipes.Broadcast;

namespace NexNet.Collections;

internal interface INexusCollectionUnion<TUnion>
    where TUnion : INexusCollectionUnion<TUnion>
{
    public NexusCollectionMessageFlags Flags { get; set; }
    
    [MemoryPackIgnore]
    int Remaining { get; set; }
    
    void Return();

    void CompleteBroadcast();

    public TUnion Clone();
    
    public INexusCollectionBroadcasterMessageWrapper<TUnion> Wrap(INexusBroadcastSession<TUnion>? client = null);
}

/// <summary>
/// Flags that control behavior of nexus collection messages.
/// </summary>
[Flags]
public enum NexusCollectionMessageFlags : byte
{
    /// <summary>
    /// No flags set.
    /// </summary>
    Unset = 0,
    
    /// <summary>
    /// Indicates that the message contains acknowledgment from the server that the operation has been completed.
    /// </summary>
    Ack = 1 << 0
}
