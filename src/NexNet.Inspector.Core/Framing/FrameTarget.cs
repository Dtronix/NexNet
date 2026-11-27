namespace NexNet.Inspector.Core.Framing;

/// <summary>What the post-header of a frame names, if anything.</summary>
public enum FrameTarget : byte
{
    /// <summary>The frame has no post-header (or it has not arrived yet).</summary>
    None = 0,

    /// <summary>A pipe ID (DuplexPipeWrite, PipeWindowUpdate).</summary>
    Pipe = 1,

    /// <summary>An invocation ID and message kind (MessageChunk).</summary>
    Invocation = 2,
}
