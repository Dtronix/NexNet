using NexNet.Diagnostics;
using NexNet.Inspector.Core.Findings;
using NexNet.Inspector.Core.Format;
using NexNet.Inspector.Core.Session;
using NexNet.Inspector.Core.Text;
using NexNet.Inspector.Tap;
using NexNet.Transports;

namespace NexNet.Inspector.Testing;

/// <summary>Options of an <see cref="InspectorCapture"/>.</summary>
public sealed class InspectorCaptureOptions
{
    /// <summary>
    /// Where <see cref="InspectorCapture.DisposeAsync"/> writes <see cref="InspectorCapture.Summary"/> and
    /// <see cref="InspectorCapture.Findings"/> (a test's output); null writes nothing.
    /// </summary>
    public TextWriter? Output { get; init; }

    /// <summary>The budget of <see cref="InspectorCapture.Summary"/> in approximate tokens; 0 for none.</summary>
    public long MaxTokens { get; init; } = TextClip.DefaultMaxTokens;

    /// <summary>The findings detectors' thresholds; <see cref="FindingsOptions.Default"/> when null.</summary>
    public FindingsOptions? Findings { get; init; }

    /// <summary>
    /// What happens when the recorder falls behind: <see cref="InspectorOverflow.Block"/> (the default here) loses nothing
    /// and slows the test's connection; <see cref="InspectorOverflow.Drop"/> records gaps.
    /// </summary>
    public InspectorOverflow Overflow { get; init; } = InspectorOverflow.Block;

    /// <summary>Record the ClientGreeting token raw instead of redacted.</summary>
    public bool IncludeSecrets { get; init; }

    /// <summary>Also write the recording (.nntl) here on dispose, to open it in the app or the CLI; null writes none.</summary>
    public string? SavePath { get; init; }
}

/// <summary>
/// Records a test's NexNet traffic in memory with the inspector (<see cref="NexusInspector.RecordTo"/>) and renders it in
/// the inspector's text format: <see cref="Summary"/> (the <c>nexnet-tap summary</c> lines) and <see cref="Findings"/>
/// (the <c>nexnet-tap findings</c> report), on demand or, with <see cref="InspectorCaptureOptions.Output"/>, on dispose.
/// </summary>
/// <remarks>
/// Start it on the server's or the client's configuration before the server or client is created, so every session is
/// recorded from its first byte. On demand while recording, the text shows what was written so far: a session still
/// connected reads as cut off there (`END truncated`), as a file being recorded does. After <see cref="StopAsync"/> it
/// is the whole recording, connected sessions closed with `END stopped`. Uses the opt-in tap; nothing is sent anywhere.
/// <code>
/// await using var capture = InspectorCapture.Start&lt;MyServerNexus&gt;(serverConfig, new() { Output = Console.Out });
/// // ... run the test ...
/// await capture.StopAsync();
/// Assert.That(capture.FindingList()).IsEmpty();
/// </code>
/// </remarks>
public sealed class InspectorCapture : IAsyncDisposable
{
    private readonly RecordingBuffer _buffer = new();
    private readonly InspectorCaptureOptions _options;
    private readonly InspectorRecording _recording;
    private bool _stopped;
    private bool _disposed;

    private InspectorCapture(NexusInspector inspector, InspectorCaptureOptions options)
    {
        Inspector = inspector;
        _options = options;
        _recording = inspector.RecordTo(_buffer, leaveOpen: true, options.Overflow);
    }

    /// <summary>The inspector installed on the configuration (its sessions, or an endpoint for the app).</summary>
    public NexusInspector Inspector { get; }

    /// <summary>True once the recording stopped (<see cref="StopAsync"/> or dispose).</summary>
    public bool IsStopped => _stopped;

    /// <summary>Records every session of the server that <paramref name="config"/> configures, without a schema (values decode raw).</summary>
    public static InspectorCapture Start(ServerConfig config, InspectorCaptureOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        options ??= new InspectorCaptureOptions();
        return new InspectorCapture(config.UseInspector(InspectorOptionsFor(options)), options);
    }

    /// <summary>Records every session of the server, decoding with <typeparamref name="TNexus"/>'s schema.</summary>
    /// <typeparam name="TNexus">The server nexus, built with <c>NexNetEmitInspectorSchema=true</c>.</typeparam>
    public static InspectorCapture Start<TNexus>(ServerConfig config, InspectorCaptureOptions? options = null)
        where TNexus : IInspectorSchemaSource
    {
        ArgumentNullException.ThrowIfNull(config);
        options ??= new InspectorCaptureOptions();
        return new InspectorCapture(config.UseInspector<TNexus>(InspectorOptionsFor(options)), options);
    }

    /// <summary>Records the connection of the client that <paramref name="config"/> configures, without a schema.</summary>
    public static InspectorCapture Start(ClientConfig config, InspectorCaptureOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        options ??= new InspectorCaptureOptions();
        return new InspectorCapture(config.UseInspector(InspectorOptionsFor(options)), options);
    }

    /// <summary>Records the client's connection, decoding with <typeparamref name="TNexus"/>'s schema.</summary>
    /// <typeparam name="TNexus">The client nexus, built with <c>NexNetEmitInspectorSchema=true</c>.</typeparam>
    public static InspectorCapture Start<TNexus>(ClientConfig config, InspectorCaptureOptions? options = null)
        where TNexus : IInspectorSchemaSource
    {
        ArgumentNullException.ThrowIfNull(config);
        options ??= new InspectorCaptureOptions();
        return new InspectorCapture(config.UseInspector<TNexus>(InspectorOptionsFor(options)), options);
    }

    /// <summary>
    /// Stops recording: what is buffered is written, sessions still connected get a close block (recording stopped), then
    /// the index footer. Later calls do nothing.
    /// </summary>
    public async Task StopAsync()
    {
        if (_stopped)
            return;
        _stopped = true;
        await _recording.StopAsync().ConfigureAwait(false);
    }

    /// <summary>The recording's bytes so far (a whole .nntl file once stopped).</summary>
    public byte[] ToArray() => _buffer.ToArray();

    /// <summary>Writes the recording so far to a .nntl file.</summary>
    public void SaveTo(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        File.WriteAllBytes(path, ToArray());
    }

    /// <summary>
    /// The <c>nexnet-tap summary</c> of every session (about 30 lines each: calls per method with p50/p99, errors,
    /// pipes, largest messages, stalls, gaps, what was open, the disconnect), within
    /// <see cref="InspectorCaptureOptions.MaxTokens"/>; "no sessions" when none connected.
    /// </summary>
    public string Summary()
    {
        var text = new TextOptions { MaxTokens = _options.MaxTokens };
        return Read(sessions =>
        {
            var results = new List<(long SessionId, List<TextRecord> Records)>();
            foreach (var session in sessions)
            {
                var records = new List<TextRecord>();
                new TextQuery(session, text).Summary(records);
                results.Add((session.SessionId, records));
            }

            using var output = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
            new TextQueryWriter(text, "summary").Write(output, results);
            return output.ToString();
        }) ?? "no sessions\n";
    }

    /// <summary>
    /// The <c>nexnet-tap findings</c> report: per session its header, ranked findings (<c>+t ! warn slow #21 …</c>) and a
    /// tally, then findings across sessions; "no sessions" when none connected.
    /// </summary>
    public string Findings() => Read(sessions =>
    {
        using var output = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        FindingsText.WriteReport(output, sessions, _options.Findings);
        return output.ToString();
    }) ?? "no sessions\n";

    /// <summary>Every finding of every session (each session's ranked, then those across sessions), for assertions.</summary>
    public IReadOnlyList<Finding> FindingList() => Read(sessions =>
    {
        var all = new List<Finding>();
        foreach (var session in sessions)
            all.AddRange(FindingsEngine.Analyze(session, _options.Findings));
        all.AddRange(FindingsEngine.AnalyzeAcross(sessions, _options.Findings));
        return all;
    }) ?? [];

    /// <summary>The whole capture in the text format (as <c>nexnet-tap tail</c> prints a file), each session in turn.</summary>
    /// <param name="options">Level, format, filters and budget; the messages level by default.</param>
    public string Text(TextOptions? options = null) => Read(sessions =>
    {
        using var output = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        for (var i = 0; i < sessions.Count; i++)
        {
            if (i > 0 && (options?.Format ?? TextFormat.Text) == TextFormat.Text)
                output.Write('\n');
            new TextRenderer(sessions[i], options).Render(output);
        }

        return output.ToString();
    }) ?? "no sessions\n";

    /// <summary>
    /// Stops recording; then writes <see cref="Summary"/> and <see cref="Findings"/> to
    /// <see cref="InspectorCaptureOptions.Output"/> and the file to <see cref="InspectorCaptureOptions.SavePath"/>, when set.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        await StopAsync().ConfigureAwait(false);
        if (_options.SavePath is { } path)
            SaveTo(path);
        if (_options.Output is { } output)
        {
            await output.WriteAsync(Summary()).ConfigureAwait(false);
            await output.WriteLineAsync().ConfigureAwait(false);
            await output.WriteAsync(Findings()).ConfigureAwait(false);
            await output.FlushAsync().ConfigureAwait(false);
        }
    }

    private static InspectorOptions InspectorOptionsFor(InspectorCaptureOptions options) => new()
    {
        AllowRecording = true,
        IncludeSecrets = options.IncludeSecrets,
        FlushInterval = TimeSpan.FromMilliseconds(20),
    };

    // Opens what was recorded so far, indexes every session and reads them; null when there is no session yet.
    private T? Read<T>(Func<List<CaptureSession>, T> read) where T : class
    {
        var bytes = _buffer.ToArray();
        if (bytes.Length == 0)
            return null;
        RecordingFile recording;
        try
        {
            recording = RecordingFile.Open(new MemoryStream(bytes, writable: false));
        }
        catch (NntlFormatException)
        {
            // Not even the file header was written yet.
            return null;
        }

        using (recording)
        {
            var sessions = new List<CaptureSession>();
            foreach (var open in recording.Sessions)
            {
                var session = recording.CreateSession(open.SessionId);
                recording.Index(session);
                sessions.Add(session);
            }

            return sessions.Count == 0 ? null : read(sessions);
        }
    }

    // The recording's destination: a memory stream the recorder writes while a test reads copies of it.
    private sealed class RecordingBuffer : Stream
    {
        private readonly Lock _gate = new();
        private readonly MemoryStream _inner = new();

        public byte[] ToArray()
        {
            lock (_gate)
                return _inner.ToArray();
        }

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            lock (_gate)
                _inner.Write(buffer);
        }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            Write(buffer.AsSpan(offset, count));
            return Task.CompletedTask;
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Write(buffer.Span);
            return ValueTask.CompletedTask;
        }

        public override void Flush()
        {
        }

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
