using System;
using System.Diagnostics.Metrics;
using System.Threading;
using NexNet.Diagnostics;

namespace NexNetBenchmarks;

/// <summary>
/// Listens to every NexNet instrument and collects the observable ones once a second, as a metrics exporter would.
/// Benchmarks run with and without one to show what an attached listener costs.
/// </summary>
internal sealed class MetricsListenerScope : IDisposable
{
    private readonly MeterListener _listener = new();
    private readonly Timer _collector;
    private long _sink;

    public MetricsListenerScope()
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == NexusMeter.Name)
                listener.EnableMeasurementEvents(instrument);
        };
        _listener.SetMeasurementEventCallback<long>((_, value, _, _) => Interlocked.Add(ref _sink, value));
        _listener.SetMeasurementEventCallback<double>((_, value, _, _) => Interlocked.Add(ref _sink, (long)value));
        _listener.Start();
        _collector = new Timer(_ => _listener.RecordObservableInstruments(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    public void Dispose()
    {
        _collector.Dispose();
        _listener.Dispose();
    }
}
