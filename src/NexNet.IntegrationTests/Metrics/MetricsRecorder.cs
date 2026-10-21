using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using NexNet.Diagnostics;

namespace NexNet.IntegrationTests.Metrics;

/// <summary>
/// A meter factory whose NexNet meter is private to one test, and a listener that records every measurement on it.
/// </summary>
internal sealed class MetricsRecorder : IMeterFactory
{
    private readonly MeterListener _listener = new();
    private readonly ConcurrentQueue<Recorded> _events = new();
    private readonly List<Recorded> _observed = new();
    private readonly Lock _observedLock = new();
    private Meter? _meter;

    public readonly record struct Recorded(string Instrument, double Value, IReadOnlyDictionary<string, object?> Tags)
    {
        public bool Has(string tag, object? value) => Tags.TryGetValue(tag, out var v) && Equals(v, value);
    }

    public MetricsRecorder()
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (ReferenceEquals(instrument.Meter, _meter))
                listener.EnableMeasurementEvents(instrument);
        };
        _listener.SetMeasurementEventCallback<long>((i, v, t, _) => Record(i, v, t));
        _listener.SetMeasurementEventCallback<double>((i, v, t, _) => Record(i, v, t));
    }

    /// <summary>
    /// The NexNet metrics on this recorder's meter. Created by the first server or client using this factory.
    /// </summary>
    public NexusMetrics Metrics
    {
        get
        {
            if (_metrics == null)
            {
                _metrics = NexusMetrics.Get(this);
                _metrics.RefreshInterval = 0;
            }

            return _metrics;
        }
    }

    private NexusMetrics? _metrics;

    public Meter Create(MeterOptions options)
    {
        if (_meter == null)
        {
            _meter = new Meter(options.Name, options.Version, options.Tags, this);
            _listener.Start();
        }

        return _meter;
    }

    private void Record(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var recorded = new Recorded(instrument.Name, value, new Dictionary<string, object?>(tags.ToArray()));
        if (!instrument.IsObservable)
        {
            _events.Enqueue(recorded);
            return;
        }

        lock (_observedLock)
            _observed.Add(recorded);
    }

    /// <summary>
    /// Events recorded by counters and histograms so far.
    /// </summary>
    public IReadOnlyList<Recorded> Events(string instrument) => _events.Where(e => e.Instrument == instrument).ToList();

    /// <summary>
    /// Collects every observable instrument once.
    /// </summary>
    public IReadOnlyList<Recorded> Observe(string instrument)
    {
        lock (_observedLock)
        {
            _observed.Clear();
            _ = Metrics;
            _listener.RecordObservableInstruments();
            return _observed.Where(e => e.Instrument == instrument).ToList();
        }
    }

    /// <summary>
    /// Sum of an observable instrument's values over the measurements matching every tag in <paramref name="tags"/>.
    /// </summary>
    public double ObserveSum(string instrument, params (string Tag, object? Value)[] tags)
        => Observe(instrument).Where(e => tags.All(t => e.Has(t.Tag, t.Value))).Sum(e => e.Value);

    public void Dispose()
    {
        _listener.Dispose();
        _meter?.Dispose();
    }
}
