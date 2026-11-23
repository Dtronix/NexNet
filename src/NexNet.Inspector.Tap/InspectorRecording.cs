namespace NexNet.Inspector.Tap;

/// <summary>A running <see cref="NexusInspector.RecordTo"/> recording.</summary>
public sealed class InspectorRecording : IAsyncDisposable
{
    private readonly TapSink _sink;

    internal InspectorRecording(TapSink sink) => _sink = sink;

    /// <summary>Completes when the recording has ended and its stream is closed (or left open, as asked).</summary>
    public Task Completion => _sink.Completion;

    /// <summary>The error that ended the recording early (the stream threw), if any.</summary>
    public Exception? Fault => _sink.Fault;

    /// <summary>
    /// Stops recording: what is buffered is written, every session still connected gets a close block (recording
    /// stopped), then the index footer.
    /// </summary>
    public async Task StopAsync()
    {
        _sink.Stop();
        await _sink.Completion.ConfigureAwait(false);
    }

    /// <summary>Same as <see cref="StopAsync"/>.</summary>
    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
