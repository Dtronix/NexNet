using System.Diagnostics;
using NexNet.Diagnostics;
using NexNet.Inspector.Core.Format;
using NexNet.Transports;

namespace NexNet.Inspector.Tap;

/// <summary>
/// The NexNet Inspector tap of one server or client: install it on the configuration (<see cref="ConfigBase.Tap"/>,
/// or <see cref="InspectorConfigExtensions.UseInspector(ServerConfig, InspectorOptions?)"/>), then serve viewers with
/// <see cref="StartEndpointAsync"/> or record with <see cref="RecordTo"/>.
/// </summary>
/// <remarks>
/// <para>
/// Every session of the configuration is tapped from its first byte, but nothing is kept until a viewer attaches or a
/// recorder runs: an unwatched session costs one volatile read per transport op. Each viewer or recorder gets its own
/// bounded ring and sender (<see cref="InspectorOptions.RingCapacity"/>), and streams <c>.nntl</c> blocks: header,
/// schema, session open, snapshot, transport ops, gaps, session close and, when it ends cleanly, the index footer.
/// </para>
/// <para>
/// The ClientGreeting auth token is redacted before any viewer or recorder sees it unless
/// <see cref="InspectorOptions.IncludeSecrets"/> is set. Recording needs <see cref="InspectorOptions.AllowRecording"/>.
/// </para>
/// </remarks>
public sealed class NexusInspector : INexusTap, IAsyncDisposable
{
    /// <summary>Version of the inspector endpoint API this build serves.</summary>
    public const int ApiVersion = 1;

    internal const string Producer = "NexNet.Inspector.Tap";
    internal const string ProducerVersion = "1";

    private readonly Lock _gate = new();
    private readonly Dictionary<long, TappedSession> _sessions = [];
    private readonly List<TapSink> _recordAll = [];
    private readonly HashSet<TapSink> _sinks = [];
    private readonly NntlFileHeader _clock = NntlFileHeader.Create(Producer, ProducerVersion);
    private long _clientConnections;
    private bool _disposed;
    private NntlSessionRole? _role;

    private NexusInspector(InspectorOptions options, byte[]? schema, byte[]? schemaHash)
    {
        Options = options;
        Schema = schema;
        SchemaHash = schemaHash;
    }

    /// <summary>Creates an inspector that streams no schema: viewers decode raw or bring their own.</summary>
    public static NexusInspector Create(InspectorOptions? options = null) => new(options ?? new InspectorOptions(), null, null);

    /// <summary>Creates an inspector that sends <typeparamref name="TNexus"/>'s schema on attach and embeds it in recordings.</summary>
    /// <typeparam name="TNexus">
    /// A generated nexus of the inspected side, built with the MSBuild property <c>NexNetEmitInspectorSchema=true</c>.
    /// </typeparam>
    public static NexusInspector Create<TNexus>(InspectorOptions? options = null)
        where TNexus : IInspectorSchemaSource =>
        new(options ?? new InspectorOptions(), TNexus.InspectorSchema.ToArray(),
            Core.Schema.SchemaHash.ToBytes(TNexus.InspectorSchemaHash));

    /// <summary>The options it was created with.</summary>
    public InspectorOptions Options { get; }

    /// <summary>The <c>.nnschema</c> bytes; empty without a schema.</summary>
    public ReadOnlyMemory<byte> Schema { get; }

    /// <summary>The schema's hash in <see cref="NexNet.Inspector.Core.Schema.SchemaHash"/> form; empty without a schema.</summary>
    public ReadOnlyMemory<byte> SchemaHash { get; }

    /// <summary>Server or client, once installed on a configuration; null before.</summary>
    public NntlSessionRole? Role => _role;

    /// <summary>
    /// Returns a session's NexNet snapshot by NexNet session ID, for the state snapshot that opens an attach. Set by
    /// <see cref="InspectorConfigExtensions.UseSnapshotsOf{TServerNexus,TClientProxy}(NexusInspector, NexusServer{TServerNexus,TClientProxy})"/>
    /// and its client overload; without it attaches carry no snapshot.
    /// </summary>
    public Func<long, NexusSessionSnapshot?>? SnapshotProvider { get; set; }

    /// <summary>Sets this inspector as the configuration's tap.</summary>
    /// <exception cref="InvalidOperationException">The inspector is installed on the other side already.</exception>
    public void Install(ConfigBase config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var role = config is ServerConfig ? NntlSessionRole.Server : NntlSessionRole.Client;
        lock (_gate)
        {
            if (_role != null && _role != role)
                throw new InvalidOperationException("An inspector taps either a server or a client, not both.");
            _role = role;
        }

        config.Tap = this;
    }

    /// <summary>The live tapped sessions.</summary>
    public InspectorSessionInfo[] GetSessions()
    {
        lock (_gate)
        {
            var result = new InspectorSessionInfo[_sessions.Count];
            var i = 0;
            foreach (var session in _sessions.Values)
                result[i++] = session.Describe(_clock);
            Array.Sort(result, static (a, b) => a.Id.CompareTo(b.Id));
            return result;
        }
    }

    /// <summary>What the endpoint reports to viewers, the recording policy included.</summary>
    public InspectorServerInfo GetServerInfo()
    {
        int count;
        lock (_gate)
            count = _sessions.Count;
        return new InspectorServerInfo(ApiVersion, _role ?? NntlSessionRole.Server, Options.AllowRecording,
            Options.IncludeSecrets, SchemaHash.IsEmpty ? null : SchemaHash.ToArray(), count);
    }

    /// <summary>
    /// Records every session into <paramref name="output"/>, one shared <c>.nntl</c> stream: the current sessions from
    /// now (with a snapshot), and every session that connects later from its first byte, preamble and greetings
    /// included. Installed before the server starts, it captures every session from inception.
    /// </summary>
    /// <param name="output">Writable destination; it need not be seekable.</param>
    /// <param name="leaveOpen">True to leave <paramref name="output"/> open when the recording stops.</param>
    /// <param name="overflow">What happens when the stream cannot keep up (Drop writes gap blocks).</param>
    /// <returns>The recording; stop or dispose it to write the index footer.</returns>
    /// <exception cref="InvalidOperationException"><see cref="InspectorOptions.AllowRecording"/> is false.</exception>
    public InspectorRecording RecordTo(Stream output, bool leaveOpen = false, InspectorOverflow overflow = InspectorOverflow.Drop)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (!Options.AllowRecording)
            throw new InvalidOperationException("Recording is disabled: set InspectorOptions.AllowRecording.");
        var sink = StartRecordAll(new StreamSinkOutput(output, leaveOpen, Options.FlushInterval), overflow, snapshot: true)
                   ?? throw new ObjectDisposedException(nameof(NexusInspector));
        return new InspectorRecording(sink);
    }

    /// <summary>
    /// Starts the inspector endpoint: a separate NexNet server, on its own listener (TCP, TLS or UDS, as
    /// <paramref name="config"/> says), that viewers and the CLI connect to with <paramref name="token"/>.
    /// </summary>
    /// <param name="config">The endpoint's own configuration. It must have no tap: the endpoint never taps itself.</param>
    /// <param name="token">The token viewers must present; compared in constant time.</param>
    /// <param name="cancellationToken">Cancels the start.</param>
    public async Task<InspectorEndpoint> StartEndpointAsync(ServerConfig config, ReadOnlyMemory<byte> token,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (token.IsEmpty)
            throw new ArgumentException("The endpoint needs a token.", nameof(token));
        if (config.Tap != null)
            throw new ArgumentException("The inspector endpoint is never tapped: its configuration must have no Tap.", nameof(config));

        config.Authenticate = true;
        var secret = token.ToArray();
        var server = new NexusServer<InspectorEndpointNexus, InspectorEndpointNexus.ClientProxy>(config,
            () => new InspectorEndpointNexus(this, secret));
        await server.StartAsync(cancellationToken).ConfigureAwait(false);
        return new InspectorEndpoint(server);
    }

    /// <summary>Ends every attach and recording (each stream gets its closing blocks and footer).</summary>
    public async ValueTask DisposeAsync()
    {
        TapSink[] sinks;
        lock (_gate)
        {
            _disposed = true;
            sinks = _sinks.ToArray();
        }

        foreach (var sink in sinks)
            sink.Stop();
        foreach (var sink in sinks)
            await sink.Completion.ConfigureAwait(false);
    }

    INexusSessionTap? INexusTap.SessionStarted(in NexusTapSessionInfo session)
    {
        var id = session.IsServer ? session.Id : Interlocked.Increment(ref _clientConnections);
        var tapped = new TappedSession(this, id, session, redact: !Options.IncludeSecrets);
        lock (_gate)
        {
            _sessions[id] = tapped;
            foreach (var sink in _recordAll)
            {
                NntlSessionOpen? open = tapped.CreateOpen(session.Timestamp, SchemaHash);
                tapped.AddLink(sink.CreateLink(tapped, open));
            }
        }

        return tapped;
    }

    internal InspectorStatus CheckAttach(InspectorAttachRequest request, out TappedSession? session)
    {
        session = null;
        lock (_gate)
        {
            if (_disposed)
                return InspectorStatus.Unavailable;
            if (request.Record && !Options.AllowRecording)
                return InspectorStatus.RecordingDisabled;

            if (request.Target == InspectorTargetKind.Session)
                return _sessions.TryGetValue(request.SessionId, out session) ? InspectorStatus.Ok : InspectorStatus.NoSuchSession;

            if (_role == NntlSessionRole.Server)
                return InspectorStatus.WrongTarget;

            // A client has one connection at a time; during a reconnection the newest is the live one.
            foreach (var candidate in _sessions.Values)
            {
                if (candidate.Role == NntlSessionRole.Client && (session == null || candidate.Id > session.Id))
                    session = candidate;
            }

            return session != null ? InspectorStatus.Ok : InspectorStatus.NoConnection;
        }
    }

    internal InspectorStatus CheckRecordAll()
    {
        lock (_gate)
        {
            if (_disposed)
                return InspectorStatus.Unavailable;
            return Options.AllowRecording ? InspectorStatus.Ok : InspectorStatus.RecordingDisabled;
        }
    }

    /// <summary>Attaches a sink to one session: from now on its ops are copied, after an open block and a snapshot.</summary>
    internal TapSink? StartAttach(TappedSession session, InspectorAttachRequest request, SinkOutput output)
    {
        var sink = new TapSink(this, output, single: true, request.Overflow, Options.RingCapacity);
        lock (_gate)
        {
            if (_disposed)
                return null;
            _sinks.Add(sink);
        }

        var link = sink.CreateLink(session, null);
        var ticks = Stopwatch.GetTimestamp();
        var open = session.CreateOpen(ticks, SchemaHash);
        if (!session.AddLink(link))
        {
            // Closed between the check and now: the stream still opens, then closes at once.
            sink.EnqueueClose(link, Messages.DisconnectReason.None, ticks);
        }

        var snapshot = request.Snapshot ? TakeSnapshot(session) : null;
        sink.Start([new InitialSession(open, snapshot, Stopwatch.GetTimestamp())]);
        return sink;
    }

    /// <summary>Starts a sink that records every session: current ones from now, later ones from inception.</summary>
    internal TapSink? StartRecordAll(SinkOutput output, InspectorOverflow overflow, bool snapshot)
    {
        var sink = new TapSink(this, output, single: false, overflow, Options.RingCapacity);
        List<TappedSession> current = [];
        lock (_gate)
        {
            if (_disposed)
                return null;
            _sinks.Add(sink);
            _recordAll.Add(sink);
            foreach (var session in _sessions.Values)
            {
                var link = sink.CreateLink(session, null);
                if (session.AddLink(link))
                    current.Add(session);
                else
                    sink.DropLink(link);
            }
        }

        var initial = new InitialSession[current.Count];
        for (var i = 0; i < initial.Length; i++)
        {
            var session = current[i];
            initial[i] = new InitialSession(session.CreateOpen(Stopwatch.GetTimestamp(), SchemaHash),
                snapshot ? TakeSnapshot(session) : null, Stopwatch.GetTimestamp());
        }

        sink.Start(initial);
        return sink;
    }

    internal void OnSessionClosed(TappedSession session)
    {
        lock (_gate)
        {
            if (_sessions.TryGetValue(session.Id, out var known) && known == session)
                _sessions.Remove(session.Id);
        }
    }

    internal void Forget(TapSink sink)
    {
        lock (_gate)
        {
            _recordAll.Remove(sink);
            _sinks.Remove(sink);
        }
    }

    /// <summary>The tapped session with <paramref name="id"/> (tests).</summary>
    internal TappedSession? FindSession(long id)
    {
        lock (_gate)
            return _sessions.GetValueOrDefault(id);
    }

    private NntlSnapshotSection[]? TakeSnapshot(TappedSession session)
    {
        var provider = SnapshotProvider;
        if (provider == null)
            return null;
        try
        {
            var snapshot = provider(session.NexusId);
            return snapshot == null ? null : NntlSnapshotSections.Create(snapshot);
        }
        catch (Exception)
        {
            // A snapshot is a convenience for decoding; the stream is still valid without one.
            return null;
        }
    }
}
