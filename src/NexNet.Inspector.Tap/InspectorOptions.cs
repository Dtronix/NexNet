namespace NexNet.Inspector.Tap;

/// <summary>Configuration of a <see cref="NexusInspector"/>: the recording policy and the per-viewer buffers.</summary>
public sealed class InspectorOptions
{
    private int _ringCapacity = 4 * 1024 * 1024;
    private TimeSpan _flushInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Allows recording: <see cref="NexusInspector.RecordTo"/>, a viewer's record-all, and attaches that declare
    /// they record. False by default, because captures can contain private data. Viewers see the policy in
    /// <see cref="InspectorServerInfo.AllowRecording"/>.
    /// </summary>
    public bool AllowRecording { get; set; }

    /// <summary>
    /// Streams and records the ClientGreeting auth token raw. False by default: the token is replaced by a same-length
    /// marker holding a SHA-256 prefix of it (<see cref="InspectorRedaction"/>) and the range is flagged as redacted.
    /// </summary>
    public bool IncludeSecrets { get; set; }

    /// <summary>
    /// Bytes of each viewer's or recorder's ring, allocated once when it starts (4 MiB by default, at least 4 KiB).
    /// Transport ops are copied into it and a background sender drains it, so the inspected connection never waits on
    /// the viewer's network unless the overflow mode is <see cref="InspectorOverflow.Block"/>.
    /// </summary>
    public int RingCapacity
    {
        get => _ringCapacity;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 4096);
            _ringCapacity = value;
        }
    }

    /// <summary>
    /// How often a <see cref="NexusInspector.RecordTo"/> recording flushes its stream while it is busy; it also
    /// flushes whenever it goes idle. One second by default.
    /// </summary>
    public TimeSpan FlushInterval
    {
        get => _flushInterval;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value, TimeSpan.Zero);
            _flushInterval = value;
        }
    }
}
