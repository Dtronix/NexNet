namespace NexNet.Inspector.Core.Framing;

/// <summary>What the first byte pushed into a <see cref="FrameParser"/> is known to be.</summary>
public enum FrameParserStart : byte
{
    /// <summary>The connection preamble: the direction was captured from the connection's first byte.</summary>
    Preamble = 0,

    /// <summary>The first byte of a frame.</summary>
    FrameBoundary = 1,

    /// <summary>
    /// Unknown: the capture began mid-stream (an attach to a running session), so the parser looks for a frame start
    /// the same way it does after a gap.
    /// </summary>
    Resync = 2,
}
