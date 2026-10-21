namespace NexNet.Diagnostics;

/// <summary>
/// Names of the <see cref="System.Diagnostics.Metrics.Meter"/> and instruments NexNet publishes. See
/// docs/articles/metrics.md for what each instrument measures and its tags.
/// </summary>
public static class NexusMeter
{
    /// <summary>
    /// Name of the meter: <c>NexNet</c>. Created through <see cref="NexNet.Transports.ConfigBase.MeterFactory"/>
    /// when one is set, otherwise one shared meter for the process.
    /// </summary>
    public const string Name = "NexNet";
}
