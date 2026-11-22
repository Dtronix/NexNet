using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Globalization;

namespace NexNet.Interop.InProc;

/// <summary>
/// The C# side of <see cref="MetricsRecording.Native"/>: a meter factory whose NexNet meter is private to one client
/// (<c>ClientConfig.MeterFactory</c>), and a listener that keeps every event measurement on it and collects the
/// observable instruments on demand.
/// </summary>
/// <remarks>
/// The C# observable instruments read a session table that NexNet refreshes at most every 100 ms, so an observed value
/// can lag that much behind the client; tests wait for the value they expect.
/// </remarks>
internal sealed class InProcMetricsRecorder : IMeterFactory
{
    private readonly MeterListener _listener = new();
    private readonly ConcurrentQueue<MetricMeasurement> _events = new();
    private readonly List<MetricMeasurement> _observed = new();
    private readonly Lock _observedLock = new();
    private readonly Lock _meterLock = new();
    private Meter? _meter;

    public InProcMetricsRecorder()
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (ReferenceEquals(instrument.Meter, _meter))
                listener.EnableMeasurementEvents(instrument);
        };
        _listener.SetMeasurementEventCallback<long>((i, v, t, _) => Record(i, v, t));
        _listener.SetMeasurementEventCallback<double>((i, v, t, _) => Record(i, v, t));
        _listener.SetMeasurementEventCallback<int>((i, v, t, _) => Record(i, v, t));
    }

    /// <inheritdoc />
    public Meter Create(MeterOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        lock (_meterLock)
        {
            if (_meter == null)
            {
                _meter = new Meter(options.Name, options.Version, options.Tags, this);
                _listener.Start();
            }

            return _meter;
        }
    }

    /// <summary>The events so far and one collection of the observable instruments.</summary>
    public ClientMetrics Read()
    {
        lock (_observedLock)
        {
            _observed.Clear();
            _listener.RecordObservableInstruments();
            return new ClientMetrics(_events.ToArray(), _observed.ToArray());
        }
    }

    private void Record(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var copy = new Dictionary<string, string>(tags.Length, StringComparer.Ordinal);
        foreach (var tag in tags)
            copy[tag.Key] = Convert.ToString(tag.Value, CultureInfo.InvariantCulture) ?? "";
        var measurement = new MetricMeasurement(instrument.Name, value, copy);
        if (!instrument.IsObservable)
        {
            _events.Enqueue(measurement);
            return;
        }

        // Observable instruments are only collected from Read, which holds the lock.
        _observed.Add(measurement);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _listener.Dispose();
        _meter?.Dispose();
    }
}
