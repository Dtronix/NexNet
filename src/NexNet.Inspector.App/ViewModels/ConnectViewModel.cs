using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NexNet.Inspector.App.Model;
using NexNet.Inspector.App.Services;
using NexNet.Inspector.App.Settings;
using NexNet.Inspector.Core.Format;
using NexNet.Inspector.Tap;

namespace NexNet.Inspector.App.ViewModels;

/// <summary>
/// The Connect dialog's model (the dialog is I17's): endpoint, token, what to inspect, overflow and snapshot, the
/// reachability probe that fills the status line (OQ18: after 500 ms without typing, or when the endpoint field loses
/// focus), and Connect, which performs the real attach. A server target lists its sessions to pick from; a client
/// target attaches to its single connection straight away.
/// </summary>
public sealed partial class ConnectViewModel : ObservableObject, IAsyncDisposable
{
    /// <summary>The typing pause before a probe.</summary>
    public static readonly TimeSpan DefaultProbeDelay = TimeSpan.FromMilliseconds(500);

    private readonly DocumentsViewModel _documents;
    private readonly TimeProvider _time;
    private CancellationTokenSource? _probe;
    private int _probeGeneration;
    private InspectorConnection? _connection;
    private readonly bool _ready;

    internal ConnectViewModel(DocumentsViewModel documents, ConnectDefaults defaults, IReadOnlyList<RecentEndpoint> recents, TimeProvider time)
    {
        _documents = documents;
        _time = time;
        RecentEndpoints = recents;
        Endpoint = defaults.Endpoint ?? "";
        Target = defaults.Target;
        Overflow = defaults.Overflow;
        Snapshot = defaults.Snapshot;

        // The saved endpoint is not probed on its own: without the token (never saved) it would only be refused. The
        // dialog probes once the user types, or on ProbeNow.
        _ready = true;
    }

    /// <summary>Recently attached endpoints, newest first.</summary>
    public IReadOnlyList<RecentEndpoint> RecentEndpoints { get; }

    /// <summary>The typing pause before a probe; tests shorten it.</summary>
    public TimeSpan ProbeDelay { get; set; } = DefaultProbeDelay;

    /// <summary>
    /// What a probe runs: connect, read the policy, disconnect. Tests and the capture harness swap in an answer of their
    /// own, so the debounce and the status line can be driven without a server.
    /// </summary>
    internal Func<string, EndpointScheme, ReadOnlyMemory<byte>, EndpointConnectOptions, CancellationToken, Task<EndpointProbeResult>> Prober { get; set; }
        = InspectorConnection.ProbeAsync;

    /// <summary>Connect and probe timeouts and the TLS certificate policy.</summary>
    public EndpointConnectOptions ConnectOptions { get; set; } = EndpointConnectOptions.Default;

    /// <summary>The endpoint as typed: <c>tcp://host:port</c>, <c>tls://host:port</c>, <c>uds:/path</c>, or <c>host:port</c> with <see cref="Transport"/>.</summary>
    [ObservableProperty]
    public partial string Endpoint { get; set; }

    /// <summary>The transport of an endpoint typed without a scheme.</summary>
    [ObservableProperty]
    public partial EndpointScheme Transport { get; set; }

    /// <summary>The endpoint's token. Never saved.</summary>
    [ObservableProperty]
    public partial string Token { get; set; } = "";

    /// <summary>Inspect a server (pick one session) or a client (its single connection).</summary>
    [ObservableProperty]
    public partial InspectorTargetKind Target { get; set; }

    /// <summary>When the viewer falls behind: Drop (gap markers) or Block (lossless, slows the connection).</summary>
    [ObservableProperty]
    public partial InspectorOverflow Overflow { get; set; }

    /// <summary>Request a state snapshot on attach.</summary>
    [ObservableProperty]
    public partial bool Snapshot { get; set; }

    /// <summary>Where the probe stands.</summary>
    [ObservableProperty]
    public partial ProbeState ProbeState { get; private set; }

    /// <summary>The status line: "Reached … · server policy: recording allowed · secrets redacted · schema provided".</summary>
    [ObservableProperty]
    public partial string? ProbeMessage { get; private set; }

    /// <summary>The server's policy from the last successful probe or connect.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RecordingAllowed))]
    public partial InspectorServerInfo? ServerInfo { get; private set; }

    /// <summary>True when the server allows recording; null until known (OQ4).</summary>
    public bool? RecordingAllowed => ServerInfo?.AllowRecording;

    /// <summary>Where Connect stands.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    public partial ConnectState ConnectState { get; private set; }

    /// <summary>Why Connect failed (the red status line).</summary>
    [ObservableProperty]
    public partial string? ConnectError { get; private set; }

    /// <summary>The server's sessions after Connect, to pick one from.</summary>
    public ObservableCollection<InspectorSessionInfo> Sessions { get; } = [];

    /// <summary>The live document the dialog produced, once attached.</summary>
    [ObservableProperty]
    public partial LiveDocumentViewModel? Document { get; private set; }

    /// <summary>The probe that is running or was last run; tests await it.</summary>
    public Task ProbeTask { get; private set; } = Task.CompletedTask;

    partial void OnEndpointChanged(string value) => ScheduleProbe(ProbeDelay);

    partial void OnTokenChanged(string value) => ScheduleProbe(ProbeDelay);

    partial void OnTransportChanged(EndpointScheme value) => ScheduleProbe(ProbeDelay);

    /// <summary>Probes at once: the endpoint field lost focus.</summary>
    [RelayCommand]
    public void ProbeNow() => ScheduleProbe(TimeSpan.Zero);

    /// <summary>
    /// Connects. A client target attaches at once and produces <see cref="Document"/>; a server target lists its
    /// sessions into <see cref="Sessions"/> for <see cref="AttachCommand"/>.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanConnect))]
    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        CancelProbe();
        ConnectError = null;
        if (!EndpointAddress.TryParse(Endpoint, Transport, out var address, out var error))
        {
            ConnectError = error;
            ConnectState = ConnectState.Failed;
            return;
        }

        ConnectState = ConnectState.Connecting;
        try
        {
            await DropConnectionAsync().ConfigureAwait(true);
            _connection = await InspectorConnection.ConnectAsync(address, TokenBytes(), ConnectOptions, cancellationToken).ConfigureAwait(true);
            ServerInfo = _connection.ServerInfo;
            ProbeState = ProbeState.Reached;
            ProbeMessage = InspectorConnection.DescribeReached(address, _connection.ServerInfo);

            if (Target == InspectorTargetKind.Client || _connection.ServerInfo.Role == NntlSessionRole.Client)
            {
                await AttachCoreAsync(null, cancellationToken).ConfigureAwait(true);
                return;
            }

            Sessions.Clear();
            foreach (var session in await _connection.ListSessionsAsync().ConfigureAwait(true))
                Sessions.Add(session);
            ConnectState = ConnectState.Connected;
        }
        catch (Exception e) when (e is InspectorConnectionException or InspectorRequestException or IOException
                                      or System.Net.Sockets.SocketException or TimeoutException or OperationCanceledException)
        {
            await DropConnectionAsync().ConfigureAwait(true);
            ConnectError = e.Message;
            ConnectState = ConnectState.Failed;
        }
    }

    /// <summary>Attaches to a session from <see cref="Sessions"/>.</summary>
    [RelayCommand]
    public async Task AttachAsync(InspectorSessionInfo session, CancellationToken cancellationToken)
    {
        try
        {
            await AttachCoreAsync(session, cancellationToken).ConfigureAwait(true);
        }
        catch (Exception e) when (e is InspectorConnectionException or InspectorRequestException or IOException
                                      or OperationCanceledException)
        {
            await DropConnectionAsync().ConfigureAwait(true);
            ConnectError = e.Message;
            ConnectState = ConnectState.Failed;
        }
    }

    /// <summary>Closes a connection that never became a document.</summary>
    public async ValueTask DisposeAsync()
    {
        CancelProbe();
        await DropConnectionAsync().ConfigureAwait(false);
    }

    private bool CanConnect() => ConnectState != ConnectState.Connecting;

    private async Task AttachCoreAsync(InspectorSessionInfo? session, CancellationToken cancellationToken)
    {
        if (_connection is not { } connection)
            throw new InvalidOperationException("Connect first.");

        // The document owns the connection from here, whatever the outcome.
        _connection = null;
        Document = await _documents.AttachAsync(connection, session, Overflow, Snapshot, cancellationToken).ConfigureAwait(true);
        ConnectState = ConnectState.Connected;
    }

    private async ValueTask DropConnectionAsync()
    {
        if (_connection is not { } connection)
            return;
        _connection = null;
        await connection.DisposeAsync().ConfigureAwait(false);
    }

    private ReadOnlyMemory<byte> TokenBytes() => Encoding.UTF8.GetBytes(Token);

    private void ScheduleProbe(TimeSpan delay)
    {
        if (!_ready)
            return;
        CancelProbe();
        if (string.IsNullOrWhiteSpace(Endpoint))
        {
            ProbeState = ProbeState.Idle;
            ProbeMessage = null;
            return;
        }

        var cancel = new CancellationTokenSource();
        _probe = cancel;
        var generation = ++_probeGeneration;
        ProbeState = ProbeState.Pending;
        ProbeTask = ProbeAsync(Endpoint, Transport, TokenBytes(), delay, generation, cancel.Token);
    }

    private void CancelProbe()
    {
        _probe?.Cancel();
        _probe?.Dispose();
        _probe = null;
    }

    private async Task ProbeAsync(string endpoint, EndpointScheme transport, ReadOnlyMemory<byte> token, TimeSpan delay,
        int generation, CancellationToken cancellationToken)
    {
        try
        {
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, _time, cancellationToken).ConfigureAwait(true);
            if (generation != _probeGeneration)
                return;

            ProbeState = ProbeState.Probing;
            var result = await Prober(endpoint, transport, token, ConnectOptions, cancellationToken)
                .ConfigureAwait(true);

            // A newer probe (more typing) owns the status line.
            if (generation != _probeGeneration)
                return;
            ProbeState = result.State;
            ProbeMessage = result.Message;
            if (result.Info is { } info)
                ServerInfo = info;
        }
        catch (OperationCanceledException)
        {
            // Superseded by more typing or by Connect.
        }
    }
}
