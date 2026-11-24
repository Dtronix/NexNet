using System.Net;
using System.Net.Sockets;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using NexNet.Inspector.App.Controls;
using NexNet.Inspector.App.Model;
using NexNet.Inspector.App.Services;
using NexNet.Inspector.App.Tests.Documents;
using NexNet.Inspector.App.ViewModels;
using NexNet.Inspector.App.Views.Connect;
using NexNet.Inspector.Tap;

namespace NexNet.Inspector.App.Tests.Dialogs;

/// <summary>
/// The Connect dialog over I18's connect model: the probe's debounce on a fake clock (500 ms after typing, at once on
/// blur, OQ18), the status line's loading / error / reached states (OQ2), and Connect attaching and closing.
/// </summary>
public sealed class ConnectDialogTests
{
    private static readonly TimeSpan Ms = TimeSpan.FromMilliseconds(1);

    private sealed class Probes
    {
        public readonly List<string> Endpoints = [];
        public ProbeState Answer = ProbeState.Reached;
        public TaskCompletionSource<EndpointProbeResult>? Pending;

        public Task<EndpointProbeResult> Probe(string endpoint, EndpointScheme scheme, ReadOnlyMemory<byte> token, EndpointConnectOptions options, CancellationToken cancel)
        {
            lock (Endpoints) Endpoints.Add(endpoint);
            if (Pending is { } pending) return pending.Task;
            string message = Answer == ProbeState.Reached ? "Reached " + endpoint + " · server policy: recording allowed" : "Could not reach " + endpoint;
            return Task.FromResult(new EndpointProbeResult(Answer, null, null, message));
        }

        public int Count
        {
            get
            {
                lock (Endpoints) return Endpoints.Count;
            }
        }
    }

    private static (ConnectDialogViewModel Dialog, Probes Probes, List<ConnectDialogViewModel> Closed) Create(DocumentsViewModel documents)
    {
        Probes probes = new();
        List<ConnectDialogViewModel> closed = [];
        ConnectViewModel connect = documents.CreateConnect();
        connect.Prober = probes.Probe;
        return (new ConnectDialogViewModel(connect, closed.Add), probes, closed);
    }

    [Test]
    public async Task The_probe_waits_500_ms_after_the_last_keystroke()
    {
        ManualTime time = new();
        await using DocumentsViewModel documents = new(new TestUi(), null, time);
        (ConnectDialogViewModel dialog, Probes probes, _) = Create(documents);
        await using ConnectDialogViewModel owned = dialog;

        dialog.Connect.Endpoint = "10.0.4.1";
        time.Advance(TimeSpan.FromMilliseconds(300));
        // More typing restarts the pause.
        dialog.Connect.Endpoint = "10.0.4.12:7711";
        time.Advance(TimeSpan.FromMilliseconds(499));
        await Assert.That(probes.Count).IsEqualTo(0);
        await Assert.That(dialog.Connect.ProbeState).IsEqualTo(ProbeState.Pending);
        await Assert.That(dialog.HasStatus).IsFalse();

        time.Advance(Ms);
        await Wait.UntilAsync(() => dialog.Connect.ProbeState == ProbeState.Reached, "the probe");
        await Assert.That(probes.Endpoints).IsEquivalentTo(["10.0.4.12:7711"]);
        await Assert.That(dialog.StatusTone).IsEqualTo(StatusTone.Ok);
        await Assert.That(dialog.StatusText).StartsWith("Reached 10.0.4.12:7711");
        await Assert.That(dialog.HasEndpointError).IsFalse();
        await Assert.That(DefaultDelay()).IsEqualTo(TimeSpan.FromMilliseconds(500));

        static TimeSpan DefaultDelay() => ConnectViewModel.DefaultProbeDelay;
    }

    [Test]
    public async Task Blur_probes_at_once_without_waiting_for_the_pause()
    {
        ManualTime time = new();
        await using DocumentsViewModel documents = new(new TestUi(), null, time);
        (ConnectDialogViewModel dialog, Probes probes, _) = Create(documents);
        await using ConnectDialogViewModel owned = dialog;

        dialog.Connect.Endpoint = "10.0.4.12:7711";
        dialog.EndpointBlurred();

        await Wait.UntilAsync(() => probes.Count == 1, "the blur probe");
        await Assert.That(dialog.Connect.ProbeState).IsEqualTo(ProbeState.Reached);
        // The pending pause was replaced, not added to: nothing more fires later.
        time.Advance(TimeSpan.FromSeconds(1));
        await Assert.That(probes.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Blur_does_not_probe_again_when_nothing_changed_since_the_last_probe()
    {
        ManualTime time = new();
        await using DocumentsViewModel documents = new(new TestUi(), null, time);
        (ConnectDialogViewModel dialog, Probes probes, _) = Create(documents);
        await using ConnectDialogViewModel owned = dialog;

        dialog.Connect.Endpoint = "10.0.4.12:7711";
        dialog.EndpointBlurred();
        await Wait.UntilAsync(() => dialog.Connect.ProbeState == ProbeState.Reached, "the blur probe");

        // Focus leaving for Connect keeps the line as it is (a new probe could move the button under the pointer, I37).
        dialog.EndpointBlurred();
        await Assert.That(probes.Count).IsEqualTo(1);
        await Assert.That(dialog.Connect.ProbeState).IsEqualTo(ProbeState.Reached);

        // A changed value is probed on blur as before.
        dialog.Connect.Token = "other";
        dialog.EndpointBlurred();
        await Wait.UntilAsync(() => probes.Count == 2, "the probe of the new token");
    }

    [Test]
    public async Task A_wrong_token_ends_the_busy_state_with_the_reason()
    {
        await using var harness = await ChatServerHarness.StartAsync(allowRecording: true);
        await harness.ConnectClientAsync();
        await harness.WaitForSessionAsync();
        await using DocumentsViewModel documents = new(new TestUi(), null);
        (ConnectDialogViewModel dialog, _, List<ConnectDialogViewModel> closed) = Create(documents);
        await using ConnectDialogViewModel owned = dialog;
        dialog.Connect.Endpoint = harness.EndpointAddress;
        dialog.Connect.Token = "not-the-token";
        bool asked = false;
        dialog.SessionChooser = (sessions, _) =>
        {
            asked = true;
            return Task.FromResult<InspectorSessionInfo?>(sessions[0]);
        };

        await dialog.SubmitCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(30));

        await Assert.That(dialog.IsBusy).IsFalse();
        await Assert.That(dialog.IsPicking).IsFalse();
        await Assert.That(dialog.StatusTone).IsEqualTo(StatusTone.Error);
        await Assert.That(dialog.StatusText).IsNotNull();
        await Assert.That(dialog.PrimaryLabel).IsEqualTo("Connect");
        await Assert.That(asked).IsFalse();
        await Assert.That(dialog.Connect.Document).IsNull();
        await Assert.That(closed).IsEmpty();
    }

    [Test]
    public async Task Several_sessions_wait_for_the_pick_and_Cancel_abandons_it()
    {
        await using var harness = await ChatServerHarness.StartAsync(allowRecording: true);
        await harness.ConnectClientAsync();
        await harness.ConnectClientAsync();
        await harness.WaitForSessionAsync(2);
        await using DocumentsViewModel documents = new(new TestUi(), null);
        (ConnectDialogViewModel dialog, _, List<ConnectDialogViewModel> closed) = Create(documents);
        await using ConnectDialogViewModel owned = dialog;
        dialog.Connect.Endpoint = harness.EndpointAddress;
        dialog.Connect.Token = ChatServerHarness.Token;
        TaskCompletionSource<CancellationToken> asked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        dialog.SessionChooser = async (sessions, cancel) =>
        {
            asked.SetResult(cancel);
            await Task.Delay(Timeout.Infinite, cancel).ContinueWith(static _ => { }, TaskScheduler.Default);
            return null;
        };

        Task submit = dialog.SubmitCommand.ExecuteAsync(null);
        CancellationToken token = await asked.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await Assert.That(dialog.IsPicking).IsTrue();
        await Assert.That(dialog.StatusTone).IsEqualTo(StatusTone.Neutral);
        await Assert.That(dialog.StatusText).IsEqualTo(ConnectDialogViewModel.PickPrompt);
        await Assert.That(dialog.PrimaryLabel).IsEqualTo("Connect");

        dialog.CancelCommand.Execute(null);
        await Assert.That(token.IsCancellationRequested).IsTrue();
        await submit.WaitAsync(TimeSpan.FromSeconds(30));
        await Assert.That(dialog.IsBusy).IsFalse();
        await Assert.That(dialog.IsPicking).IsFalse();
        await Assert.That(closed.Count).IsEqualTo(1);
        await Assert.That(documents.Documents.Count).IsEqualTo(0);
    }

    [Test]
    [NotInParallel("avalonia")]
    public async Task The_endpoint_field_losing_focus_probes()
    {
        ManualTime time = new();
        await using DocumentsViewModel documents = new(new TestUi(), null, time);
        (ConnectDialogViewModel dialog, Probes probes, _) = Create(documents);
        await using ConnectDialogViewModel owned = dialog;
        dialog.Connect.Endpoint = "10.0.4.12:7711";

        await HeadlessSession.Run(() =>
        {
            ConnectDialog view = new() { DataContext = dialog };
            Window window = new() { Width = 700, Height = 700, Content = view };
            window.Show();
            try
            {
                UiPump.Frames();
                view.FindControl<TextBox>("EndpointBox")!.Focus();
                view.FindControl<TextBox>("TokenBox")!.Focus();
                UiPump.Frames();
            }
            finally
            {
                window.Close();
            }
        });

        await Wait.UntilAsync(() => probes.Count == 1, "the blur probe");
        await Assert.That(time.ActiveTimers).IsEqualTo(0);
    }

    [Test]
    public async Task Probing_shows_the_spinner_and_a_failed_probe_the_error()
    {
        ManualTime time = new();
        await using DocumentsViewModel documents = new(new TestUi(), null, time);
        (ConnectDialogViewModel dialog, Probes probes, _) = Create(documents);
        await using ConnectDialogViewModel owned = dialog;

        probes.Pending = new TaskCompletionSource<EndpointProbeResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        dialog.Connect.Endpoint = "10.0.4.12:7711";
        dialog.EndpointBlurred();
        await Assert.That(dialog.StatusTone).IsEqualTo(StatusTone.Busy);
        await Assert.That(dialog.StatusText).IsEqualTo("Checking 10.0.4.12:7711…");

        probes.Pending.SetResult(new EndpointProbeResult(ProbeState.Unreachable, null, null, "Could not reach tcp://10.0.4.12:7711"));
        await Wait.UntilAsync(() => dialog.Connect.ProbeState == ProbeState.Unreachable, "the failed probe");
        await Assert.That(dialog.StatusTone).IsEqualTo(StatusTone.Error);
        await Assert.That(dialog.StatusText).IsEqualTo("Could not reach tcp://10.0.4.12:7711");
        await Assert.That(dialog.HasEndpointError).IsTrue();

        // Typing again clears it until the next probe answers.
        probes.Pending = null;
        dialog.Connect.Endpoint = "10.0.4.12:7712";
        await Assert.That(dialog.HasStatus).IsFalse();
        await Assert.That(dialog.HasEndpointError).IsFalse();
    }

    [Test]
    public async Task Connect_shows_connecting_then_the_reason_it_failed_and_stays_open()
    {
        // A port that accepts and never answers holds the connect; a closed one refuses it.
        using TcpListener silent = new(IPAddress.Loopback, 0);
        silent.Start();
        int silentPort = ((IPEndPoint)silent.LocalEndpoint).Port;

        await using DocumentsViewModel documents = new(new TestUi(), null);
        (ConnectDialogViewModel dialog, _, List<ConnectDialogViewModel> closed) = Create(documents);
        await using ConnectDialogViewModel owned = dialog;
        dialog.Connect.ConnectOptions = new EndpointConnectOptions(TimeSpan.FromSeconds(1));
        dialog.Connect.Endpoint = "127.0.0.1:" + silentPort;

        Task submit = dialog.SubmitCommand.ExecuteAsync(null);
        await Assert.That(dialog.IsBusy).IsTrue();
        await Assert.That(dialog.PrimaryLabel).IsEqualTo("Connecting…");
        await Assert.That(dialog.StatusTone).IsEqualTo(StatusTone.Busy);
        await Assert.That(dialog.StatusText).IsEqualTo($"Connecting to 127.0.0.1:{silentPort}…");
        await submit;

        await Assert.That(dialog.IsBusy).IsFalse();
        await Assert.That(dialog.PrimaryLabel).IsEqualTo("Connect");
        await Assert.That(dialog.StatusTone).IsEqualTo(StatusTone.Error);
        await Assert.That(dialog.StatusText).IsNotNull();
        await Assert.That(dialog.HasEndpointError).IsTrue();
        await Assert.That(closed).IsEmpty();
    }

    [Test]
    public async Task Cancel_stops_a_connect_under_way_and_closes()
    {
        using TcpListener silent = new(IPAddress.Loopback, 0);
        silent.Start();
        await using DocumentsViewModel documents = new(new TestUi(), null);
        (ConnectDialogViewModel dialog, _, List<ConnectDialogViewModel> closed) = Create(documents);
        await using ConnectDialogViewModel owned = dialog;
        dialog.Connect.ConnectOptions = new EndpointConnectOptions(TimeSpan.FromSeconds(2));
        dialog.Connect.Endpoint = "127.0.0.1:" + ((IPEndPoint)silent.LocalEndpoint).Port;

        Task submit = dialog.SubmitCommand.ExecuteAsync(null);
        dialog.CancelCommand.Execute(null);

        // Closed at once, whether or not the handshake under way honours the cancellation.
        await Assert.That(closed.Count).IsEqualTo(1);
        await Assert.That(closed[0]).IsSameReferenceAs(dialog);
        await Assert.That(dialog.IsClosed).IsTrue();
        await submit.WaitAsync(TimeSpan.FromSeconds(20));
        await Assert.That(dialog.Connect.Document).IsNull();
        await Assert.That(closed.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Connect_attaches_to_a_server_session_and_closes()
    {
        await using var harness = await ChatServerHarness.StartAsync(allowRecording: true);
        await harness.ConnectClientAsync();
        await harness.WaitForSessionAsync();
        await using DocumentsViewModel documents = new(new TestUi(), null);
        (ConnectDialogViewModel dialog, _, List<ConnectDialogViewModel> closed) = Create(documents);
        await using ConnectDialogViewModel owned = dialog;
        dialog.Connect.Endpoint = harness.EndpointAddress;
        dialog.Connect.Token = ChatServerHarness.Token;
        dialog.IsServer = true;
        IReadOnlyList<InspectorSessionInfo>? offered = null;
        dialog.SessionChooser = (sessions, _) =>
        {
            offered = sessions;
            return Task.FromResult<InspectorSessionInfo?>(sessions[0]);
        };

        await dialog.SubmitCommand.ExecuteAsync(null);

        await Assert.That(dialog.Connect.Document).IsNotNull().Because(dialog.StatusText ?? "");
        // One session is the only answer: attached without asking (I37).
        await Assert.That(offered).IsNull();
        await Assert.That(documents.Active).IsSameReferenceAs(dialog.Connect.Document);
        await Assert.That(closed.Count).IsEqualTo(1);
        await Assert.That(closed[0]).IsSameReferenceAs(dialog);
    }

    [Test]
    public async Task The_choices_follow_the_dialog_controls()
    {
        await using DocumentsViewModel documents = new(new TestUi(), null);
        (ConnectDialogViewModel dialog, _, _) = Create(documents);
        await using ConnectDialogViewModel owned = dialog;

        dialog.IsClient = true;
        dialog.Overflow.SelectedIndex = 1;
        dialog.Transport.SelectedIndex = 1;

        await Assert.That(dialog.Connect.Target).IsEqualTo(InspectorTargetKind.Client);
        await Assert.That(dialog.IsServer).IsFalse();
        await Assert.That(dialog.Connect.Overflow).IsEqualTo(InspectorOverflow.Block);
        await Assert.That(dialog.Connect.Transport).IsEqualTo(EndpointScheme.Tls);
        await Assert.That(dialog.Overflow.Labels).IsEquivalentTo(["Drop · show gap markers", "Block · lossless, slows the connection"]);
    }
}
