using NexNet.Pipes.NexusStream.Frames;

namespace NexNet.Pipes.NexusStream;

/// <summary>
/// Implementation of <see cref="INexusStreamRequest"/> created from an Open frame.
/// </summary>
internal sealed class NexusStreamRequest : INexusStreamRequest
{
    /// <inheritdoc />
    public string ResourceId { get; }

    /// <inheritdoc />
    public StreamAccessMode Access { get; }

    /// <inheritdoc />
    public StreamShareMode Share { get; }

    /// <inheritdoc />
    public long ResumePosition { get; }

    /// <summary>
    /// Creates a new stream request from an Open frame.
    /// </summary>
    /// <param name="openFrame">The Open frame to create the request from.</param>
    internal NexusStreamRequest(OpenFrame openFrame)
    {
        ResourceId = openFrame.ResourceId;
        Access = openFrame.Access;
        Share = openFrame.Share;
        ResumePosition = openFrame.ResumePosition;
    }

    /// <summary>
    /// Creates a new stream request with the specified properties.
    /// </summary>
    internal NexusStreamRequest(string resourceId, StreamAccessMode access, StreamShareMode share, long resumePosition)
    {
        ResourceId = resourceId;
        Access = access;
        Share = share;
        ResumePosition = resumePosition;
    }

    /// <inheritdoc />
    public override string ToString() =>
        $"StreamRequest {{ ResourceId = \"{ResourceId}\", Access = {Access}, Share = {Share}, ResumePosition = {ResumePosition} }}";
}
