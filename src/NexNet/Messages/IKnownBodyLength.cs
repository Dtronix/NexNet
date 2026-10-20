namespace NexNet.Messages;

/// <summary>
/// A message that knows the length of its serialized body before writing it, so a session can write the frame header
/// first and the body straight to the output without staging it.
/// </summary>
internal interface IKnownBodyLength
{
    /// <summary>
    /// Exact number of bytes <see cref="IMessageBase.Serialize"/> writes.
    /// </summary>
    int BodyLength { get; }
}
