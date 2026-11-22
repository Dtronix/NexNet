namespace NexNet.Interop;

/// <summary>
/// How a client under test records its metrics for <see cref="IClientUnderTest.GetMetricsAsync"/>
/// (<see cref="ClientSpec.Metrics"/>).
/// </summary>
public enum MetricsRecording
{
    /// <summary>
    /// The runtime's own metrics API: C# <c>ClientConfig.MeterFactory</c> with a meter private to the client, Java and
    /// JavaScript <c>NexusMetrics</c> tracking the client, with a listener for the event instruments.
    /// </summary>
    Native,

    /// <summary>
    /// The Java client's Micrometer module (<c>NexusMicrometerBinder</c> on a <c>SimpleMeterRegistry</c>), read back
    /// from the registry. Java only; the other languages refuse it (<c>NotSupported</c>).
    /// </summary>
    Micrometer,
}
