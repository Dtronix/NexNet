namespace NexNet.Interop;

/// <summary>
/// One measurement of a NexNet instrument (the C# meter's names and tags, docs/articles/metrics.md).
/// </summary>
/// <param name="Instrument">The instrument, such as <c>nexnet.io.frames</c>.</param>
/// <param name="Value">
/// A counter's increment or a histogram's value (an event), or an observable instrument's value (cumulative for an
/// observable counter, the current level for an up-down counter). Aggregated by
/// <see cref="MetricsRecording.Micrometer"/>: a counter's total, a histogram's sum.
/// </param>
/// <param name="Tags">The tags, every value as text.</param>
/// <param name="Count">
/// How many recordings the measurement stands for: 1, except for a histogram read from Micrometer, which is the number
/// of values recorded.
/// </param>
public sealed record MetricMeasurement(string Instrument, double Value, IReadOnlyDictionary<string, string> Tags, long Count = 1)
{
    /// <summary>Whether the measurement has the tag <paramref name="tag"/> with the value <paramref name="value"/>.</summary>
    public bool Has(string tag, string value) => Tags.TryGetValue(tag, out var v) && v == value;

    /// <summary>Whether the measurement has every tag of <paramref name="tags"/>.</summary>
    public bool HasAll(params (string Tag, string Value)[] tags) => tags.All(t => Has(t.Tag, t.Value));

    /// <inheritdoc />
    public override string ToString()
        => $"{Instrument}={Value}{(Count == 1 ? "" : $" (x{Count})")} {{{string.Join(", ", Tags.Select(t => t.Key + "=" + t.Value))}}}";
}

/// <summary>
/// The metrics a client under test recorded (<see cref="IClientUnderTest.GetMetricsAsync"/>, control protocol §6.4
/// <c>client.metrics</c>): its event instruments since it was created, and its observable instruments now.
/// </summary>
/// <param name="Events">
/// The counters' increments and the histograms' values (<c>nexnet.session.disconnects</c>, <c>nexnet.pipe.resets</c>,
/// <c>nexnet.invocation.queue_wait</c>, ...), in the order they happened. Aggregated per instrument and tag set when
/// read from Micrometer.
/// </param>
/// <param name="Observed">
/// One collection of the observable instruments (<c>nexnet.io.frames</c>, <c>nexnet.pipe.open</c>,
/// <c>nexnet.budget.reserved</c>, ...): one measurement per instrument and tag set.
/// </param>
public sealed record ClientMetrics(IReadOnlyList<MetricMeasurement> Events, IReadOnlyList<MetricMeasurement> Observed)
{
    /// <summary>The events of <paramref name="instrument"/> that have every tag of <paramref name="tags"/>.</summary>
    public IReadOnlyList<MetricMeasurement> EventsOf(string instrument, params (string Tag, string Value)[] tags)
        => Events.Where(e => e.Instrument == instrument && e.HasAll(tags)).ToList();

    /// <summary>How many values the events of <paramref name="instrument"/> with every tag of <paramref name="tags"/> stand for.</summary>
    public long CountOf(string instrument, params (string Tag, string Value)[] tags)
        => EventsOf(instrument, tags).Sum(e => e.Count);

    /// <summary>The sum of the observed values of <paramref name="instrument"/> with every tag of <paramref name="tags"/>.</summary>
    public double Observe(string instrument, params (string Tag, string Value)[] tags)
        => Observed.Where(e => e.Instrument == instrument && e.HasAll(tags)).Sum(e => e.Value);

    /// <inheritdoc />
    public override string ToString()
        => "events: [" + string.Join("; ", Events) + "], observed: [" + string.Join("; ", Observed) + "]";
}
