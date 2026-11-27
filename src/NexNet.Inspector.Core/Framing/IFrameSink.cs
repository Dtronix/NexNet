namespace NexNet.Inspector.Core.Framing;

/// <summary>Receives the records a <see cref="FrameParser"/> produces.</summary>
/// <remarks>
/// The parser methods are generic over the sink so a struct sink is called without boxing or a virtual call per frame.
/// </remarks>
public interface IFrameSink
{
    /// <summary>Called once per record, in stream order.</summary>
    /// <param name="frame">The record. It is a copy; the parser keeps no reference to it.</param>
    void Add(in FrameRecord frame);
}
