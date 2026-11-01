using NexNet.Serialization;

namespace NexNet.Messages;

/// <summary>
/// A message body whose exact encoded length is known before it is written, so a session can write the frame
/// header and then the body straight to the transport output, without staging the body to measure it.
/// Implemented by readonly snapshot structs (<see cref="InvocationBody"/>, <see cref="InvocationResultBody"/>):
/// the length and the bytes come from the same values.
/// </summary>
internal interface IFrameBody
{
    /// <summary>
    /// Exact number of bytes <see cref="Write"/> writes.
    /// </summary>
    int Length { get; }

    /// <summary>
    /// Writes the body.
    /// </summary>
    void Write(ref MsgPackWriter writer);
}
