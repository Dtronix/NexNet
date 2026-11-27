namespace NexNet.Inspector.Core.Framing;

/// <summary>What a <see cref="FrameRecord"/> covers in the stream.</summary>
public enum FrameKind : byte
{
    /// <summary>A protocol frame (§2): type, optional length and post-header, body.</summary>
    Frame = 0,

    /// <summary>The 8-byte connection preamble (§1), recorded as a pseudo-frame so every stream byte belongs to a record.</summary>
    Preamble = 1,

    /// <summary>
    /// Bytes the parser could not frame: an unknown type byte or a preamble with the wrong magic or reserved bytes. The
    /// record covers the rest of the op it was found in; the parser frames nothing after it (see <see cref="FrameParser"/>).
    /// </summary>
    Malformed = 2,
}
