namespace NexNet.Inspector.Core.Framing;

/// <summary>Annotations on a <see cref="FrameRecord"/>; each one maps to a chip letter in the viewer.</summary>
/// <remarks>
/// The parser sets only <see cref="TransportSplit"/> and <see cref="Incomplete"/>. Message assembly adds
/// <see cref="NexNetSplit"/> and <see cref="Cancel"/>, and a capture session adds <see cref="Redacted"/> to a frame whose
/// bytes overlap a range its op store recorded as redacted, all on the same record without a second flags type.
/// </remarks>
[Flags]
public enum FrameFlags : byte
{
    /// <summary>No annotation.</summary>
    None = 0,

    /// <summary>`T`: the frame's bytes arrived in, or were written by, more than one transport op.</summary>
    TransportSplit = 1 << 0,

    /// <summary>`N`: the frame carries part of a message NexNet split across frames.</summary>
    NexNetSplit = 1 << 1,

    /// <summary>`…`: the stream ended, or has not yet delivered, the rest of the frame.</summary>
    Incomplete = 1 << 2,

    /// <summary>`X`: the frame cancels an invocation or carries a canceled result.</summary>
    Cancel = 1 << 3,

    /// <summary>`R`: part of the frame's bytes were redacted before capture.</summary>
    Redacted = 1 << 4,
}
