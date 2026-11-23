using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NexNet.Inspector.App.Controls;
using NexNet.Inspector.App.Controls.Primitives;
using NexNet.Inspector.App.Model;
using NexNet.Inspector.App.Services;
using NexNet.Inspector.Tap;

namespace NexNet.Inspector.App.ViewModels;

/// <summary>
/// The Connect dialog (connect screen) over I18's <see cref="ConnectViewModel"/>: the choices as the dialog's controls
/// bind them (radio cards, the Transport select, the Drop | Block segmented row) and one status line for the probe and
/// the connect step (OQ2): a spinner with "Connecting…" while busy, the policy in green once reached, the reason in red
/// with an error border on the endpoint when it failed. Connect performs the real attach; on a server the session is
/// chosen by <see cref="SessionChooser"/> (I28's session popover replaces the default, which takes the first session).
/// The dialog closes itself once a document is attached, or on Cancel.
/// </summary>
public sealed partial class ConnectDialogViewModel : ObservableObject, IAsyncDisposable
{
    private readonly Action<ConnectDialogViewModel> _close;
    private string? _failure;
    private bool _showConnectOutcome;
    private bool _closed;

    /// <param name="connect">The connect model (I18), owned by this dialog from now on.</param>
    /// <param name="close">Removes the dialog from the window; called once, on success or cancel.</param>
    public ConnectDialogViewModel(ConnectViewModel connect, Action<ConnectDialogViewModel> close)
    {
        ArgumentNullException.ThrowIfNull(connect);
        ArgumentNullException.ThrowIfNull(close);
        Connect = connect;
        _close = close;
        Transport = new EnumChoice<EndpointScheme>([(EndpointScheme.Tcp, "TCP"), (EndpointScheme.Tls, "TLS"), (EndpointScheme.Uds, "UDS")], connect.Transport);
        Overflow = new EnumChoice<InspectorOverflow>(
            [(InspectorOverflow.Drop, "Drop · show gap markers"), (InspectorOverflow.Block, "Block · lossless, slows the connection")], connect.Overflow);
        Transport.ValueChanged += (_, value) => Connect.Transport = value;
        Overflow.ValueChanged += (_, value) => Connect.Overflow = value;
        connect.PropertyChanged += OnConnectChanged;
    }

    /// <summary>The endpoint, token, snapshot and probe (bound directly).</summary>
    public ConnectViewModel Connect { get; }

    public string Title => "Connect to inspector";

    public string Hint => "Enter to connect · Esc to cancel";

    /// <summary>The Transport select: the scheme of an endpoint typed without one.</summary>
    public EnumChoice<EndpointScheme> Transport { get; }

    /// <summary>When the viewer falls behind: Drop | Block.</summary>
    public EnumChoice<InspectorOverflow> Overflow { get; }

    /// <summary>The "Server" radio card: pick any one connected session.</summary>
    public bool IsServer
    {
        get => Connect.Target != InspectorTargetKind.Client;
        set
        {
            if (value) Connect.Target = InspectorTargetKind.Session;
        }
    }

    /// <summary>The "Client" radio card: its single connection.</summary>
    public bool IsClient
    {
        get => Connect.Target == InspectorTargetKind.Client;
        set
        {
            if (value) Connect.Target = InspectorTargetKind.Client;
        }
    }

    /// <summary>
    /// Picks the session to attach to on a server, or null to stay in the dialog. The default takes the first one;
    /// I28's session popover replaces it.
    /// </summary>
    public Func<IReadOnlyList<InspectorSessionInfo>, Task<InspectorSessionInfo?>> SessionChooser { get; set; } =
        static sessions => Task.FromResult<InspectorSessionInfo?>(sessions.Count > 0 ? sessions[0] : null);

    /// <summary>Connect was pressed and has not finished: the spinner, "Connecting…" on the primary button.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PrimaryLabel))]
    public partial bool IsBusy { get; private set; }

    public string PrimaryLabel => IsBusy ? "Connecting…" : "Connect";

    /// <summary>The status line's dot (or spinner).</summary>
    public StatusTone StatusTone => Status().Tone;

    /// <summary>The status line's text; null hides the line.</summary>
    public string? StatusText => Status().Text;

    public bool HasStatus => StatusText is not null;

    /// <summary>The endpoint field shows the 1.5 px error border (OQ2).</summary>
    public bool HasEndpointError => StatusTone == StatusTone.Error;

    /// <summary>Closed: attached, or canceled.</summary>
    public bool IsClosed => _closed;

    /// <summary>The primary button and Enter: connect, choose a session on a server, attach, close.</summary>
    [RelayCommand]
    private async Task Submit()
    {
        if (IsBusy || _closed) return;
        _failure = null;
        _showConnectOutcome = true;
        IsBusy = true;
        RaiseStatus();
        try
        {
            await Connect.ConnectCommand.ExecuteAsync(null).ConfigureAwait(true);
            if (_closed)
            {
                // Canceled while the connect ran on (a handshake does not always honour cancellation): let it go.
                await Connect.DisposeAsync().ConfigureAwait(true);
                return;
            }

            if (Connect.Document is null && Connect.ConnectState == ConnectState.Connected)
            {
                if (Connect.Sessions.Count == 0)
                {
                    _failure = "No session is connected to this server yet";
                }
                else if (await SessionChooser(Connect.Sessions).ConfigureAwait(true) is { } session)
                {
                    await Connect.AttachCommand.ExecuteAsync(session).ConfigureAwait(true);
                }
            }
        }
        finally
        {
            IsBusy = false;
            RaiseStatus();
        }

        if (Connect.Document is not null) Close();
    }

    /// <summary>Cancel, ✕ and Esc: stops a connect under way and closes.</summary>
    [RelayCommand]
    private void Cancel()
    {
        if (Connect.ConnectCommand.IsRunning) Connect.ConnectCommand.Cancel();
        if (Connect.AttachCommand.IsRunning) Connect.AttachCommand.Cancel();
        Close();
    }

    /// <summary>The endpoint field lost focus: probe now rather than after the typing pause (OQ18).</summary>
    public void EndpointBlurred()
    {
        if (!_closed && !IsBusy) Connect.ProbeNow();
    }

    public async ValueTask DisposeAsync()
    {
        Connect.PropertyChanged -= OnConnectChanged;
        await Connect.DisposeAsync().ConfigureAwait(false);
    }

    private void Close()
    {
        if (_closed) return;
        _closed = true;
        _close(this);
    }

    private (StatusTone Tone, string? Text) Status()
    {
        if (IsBusy) return (StatusTone.Busy, $"Connecting to {Connect.Endpoint.Trim()}…");
        if (_failure is { } failure) return (StatusTone.Error, failure);
        if (_showConnectOutcome && Connect.ConnectState == ConnectState.Failed)
            return (StatusTone.Error, Connect.ConnectError ?? $"Could not connect to {Connect.Endpoint.Trim()}");
        return Connect.ProbeState switch
        {
            ProbeState.Probing => (StatusTone.Busy, $"Checking {Connect.Endpoint.Trim()}…"),
            ProbeState.Reached => (StatusTone.Ok, Connect.ProbeMessage),
            ProbeState.Invalid or ProbeState.Unreachable or ProbeState.Refused => (StatusTone.Error, Connect.ProbeMessage),
            _ => (StatusTone.Neutral, null),
        };
    }

    private void OnConnectChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ConnectViewModel.Endpoint) or nameof(ConnectViewModel.Token) or nameof(ConnectViewModel.Transport):
                // An edit makes the last connect's outcome stale; the probe speaks for the new values.
                _showConnectOutcome = false;
                _failure = null;
                RaiseStatus();
                break;
            case nameof(ConnectViewModel.Target):
                OnPropertyChanged(nameof(IsServer));
                OnPropertyChanged(nameof(IsClient));
                break;
            case nameof(ConnectViewModel.ProbeState) or nameof(ConnectViewModel.ProbeMessage)
                or nameof(ConnectViewModel.ConnectState) or nameof(ConnectViewModel.ConnectError):
                RaiseStatus();
                break;
        }
    }

    private void RaiseStatus()
    {
        OnPropertyChanged(nameof(StatusTone));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(HasStatus));
        OnPropertyChanged(nameof(HasEndpointError));
    }
}
