using NexNet.Inspector.App.Model;
using NexNet.Inspector.App.Services;
using NexNet.Inspector.App.Settings;
using NexNet.Inspector.App.Tests.Documents;
using NexNet.Inspector.App.ViewModels;
using NexNet.Inspector.Core.Format;
using NexNet.Inspector.Core.Session;
using NexNet.Inspector.Tap;

namespace NexNet.Inspector.App.Tests.Shell;

/// <summary>
/// The title bar's session area and the session popover against a live tapped Chat server: the connection group's words,
/// the server's sessions, switching session, Record all into one .nntl (ALL · n), the disabled record state (OQ4), the
/// REC timer, and the Connect dialog's session pick through the popover.
/// </summary>
public class SessionAreaTests
{
    private static async Task<LiveDocumentViewModel> AttachAsync(DocumentsViewModel documents, ChatServerHarness harness, int session = 0)
    {
        await using var connect = documents.CreateConnect();
        connect.Endpoint = harness.EndpointAddress;
        connect.Token = ChatServerHarness.Token;
        await connect.ConnectCommand.ExecuteAsync(null);
        await Assert.That(connect.ConnectState).IsEqualTo(ConnectState.Connected).Because(connect.ConnectError ?? "");
        await connect.AttachCommand.ExecuteAsync(connect.Sessions[session]);
        return connect.Document ?? throw new InvalidOperationException("attach failed: " + connect.ConnectError);
    }

    [Test]
    public async Task The_area_follows_the_live_document_and_lists_the_servers_sessions()
    {
        await using var harness = await ChatServerHarness.StartAsync(allowRecording: true);
        await harness.ConnectClientAsync();
        await harness.ConnectClientAsync();
        await harness.WaitForSessionAsync(2);
        await using var documents = new DocumentsViewModel(new TestUi(), null);
        var area = new SessionAreaViewModel(documents);
        await Assert.That(area.HasConnection).IsFalse();

        var live = await AttachAsync(documents, harness);
        await Assert.That(area.Live).IsSameReferenceAs(live);
        await Assert.That(area.HasLive).IsTrue();
        await Assert.That(area.RoleLabel).IsEqualTo("Server");
        await Assert.That(area.EndpointText).IsEqualTo(SessionAreaViewModel.Display(live.Endpoint));
        await Assert.That(area.EndpointText.Contains("://", StringComparison.Ordinal)).IsFalse();
        await Assert.That(area.SessionText).IsEqualTo(LiveDocumentViewModel.SessionHex(live.AttachedSession!.Value.Id));
        await Assert.That(area.IsOnline).IsTrue();
        await Assert.That(area.RecordingAllowed).IsTrue();
        await Assert.That(area.PolicyText).IsEqualTo("Recording enabled by server config");
        await Assert.That(area.RecordTooltip).IsEqualTo(SessionAreaViewModel.RecordAllTooltip);

        area.IsPopoverOpen = true;
        await Wait.UntilAsync(() => area.Sessions.Count == 2, "the session list");
        await Assert.That(area.ConnectedText).IsEqualTo("2 connected");
        await Assert.That(area.Sessions.Count(s => s.IsAttached)).IsEqualTo(1);
        await Assert.That(area.Sessions.Single(s => s.IsAttached).Info.Id).IsEqualTo(live.AttachedSession!.Value.Id);
        await Assert.That(area.Sessions.All(s => s.IsConnected && !s.IsRecording && s.Uptime.Contains(':', StringComparison.Ordinal))).IsTrue();
        await Assert.That(area.RecordAllDetail).IsEqualTo("2 now + any that connect");

        // A row switches the document to that session (detach, then attach).
        var other = area.Sessions.Single(s => !s.IsAttached);
        await area.ChooseCommand.ExecuteAsync(other);
        await Assert.That(area.Error).IsNull();
        await Assert.That(live.AttachedSession!.Value.Id).IsEqualTo(other.Info.Id);
        await Assert.That(live.State).IsEqualTo(LiveState.Live);
        await Assert.That(area.IsPopoverOpen).IsFalse();
        await Assert.That(area.SessionText).IsEqualTo(other.IdText);
        await area.DisposeAsync();
    }

    [Test]
    public async Task Record_all_writes_every_session_into_one_recording_and_shows_ALL_n()
    {
        await using var harness = await ChatServerHarness.StartAsync(allowRecording: true);
        await harness.ConnectClientAsync();
        await harness.ConnectClientAsync();
        await harness.WaitForSessionAsync(2);
        using var temp = new TempDirectory();
        var store = new SettingsStore(temp.File("settings.json"));
        await using var documents = new DocumentsViewModel(new TestUi(), store);
        var area = new SessionAreaViewModel(documents) { RecordingDirectory = () => temp.File("recordings") };
        await AttachAsync(documents, harness);
        await area.RefreshSessionsAsync();

        await Assert.That(area.RecordAllCommand.CanExecute(null)).IsTrue();
        await area.RecordAllCommand.ExecuteAsync(null);
        await Assert.That(area.Error).IsNull();
        await Assert.That(area.IsRecordingAll).IsTrue();
        await Assert.That(area.RecordText).IsEqualTo("ALL · 2");
        await Assert.That(area.RecordAllLabel).IsEqualTo("Stop recording");
        await Assert.That(area.Sessions.All(s => s.IsRecording)).IsTrue();

        // A session that connects while recording joins it.
        var late = await harness.ConnectClientAsync();
        await harness.WaitForSessionAsync(3);
        await late.Proxy.Join("late");
        await area.RefreshSessionsAsync();
        await Assert.That(area.RecordText).IsEqualTo("ALL · 3");

        await area.RecordAllCommand.ExecuteAsync(null);
        await Assert.That(area.IsRecordingAll).IsFalse();
        var path = area.LastRecording!;
        await Assert.That(path).StartsWith(temp.File("recordings"));
        await Assert.That(Path.GetFileName(path)).StartsWith("server-all-");
        using (var recording = RecordingFile.Open(path))
        {
            await Assert.That(recording.Sessions.Count).IsEqualTo(3);
            await Assert.That(recording.Warnings).IsEqualTo(RecordingWarnings.None);
        }

        await Assert.That(store.Current.RecentFiles[0].Path).IsEqualTo(path);
        await Assert.That(store.Current.RecentFiles[0].Sessions).IsEqualTo(3);
        await area.DisposeAsync();
    }

    [Test]
    public async Task Recording_disabled_by_the_server_disables_the_record_controls_with_their_tooltip()
    {
        await using var harness = await ChatServerHarness.StartAsync(allowRecording: false);
        await harness.ConnectClientAsync();
        await harness.WaitForSessionAsync();
        await using var documents = new DocumentsViewModel(new TestUi(), null);
        var area = new SessionAreaViewModel(documents);
        await AttachAsync(documents, harness);

        await Assert.That(area.HasLive).IsTrue();
        await Assert.That(area.RecordingAllowed).IsFalse();
        await Assert.That(area.RecordAllCommand.CanExecute(null)).IsFalse();
        await Assert.That(area.RecordTooltip).IsEqualTo("Recording disabled by server config");
        await Assert.That(area.PolicyText).IsEqualTo("Recording disabled by server config");
        await area.DisposeAsync();
    }

    [Test]
    public async Task The_REC_timer_counts_from_the_attach()
    {
        await using var harness = await ChatServerHarness.StartAsync(allowRecording: true);
        await harness.ConnectClientAsync();
        await harness.WaitForSessionAsync();
        var time = new ManualTime();
        await using var documents = new DocumentsViewModel(new TestUi(), null, time);
        var area = new SessionAreaViewModel(documents);
        var live = await AttachAsync(documents, harness);

        await Assert.That(live.AttachedAt).IsEqualTo(time.GetUtcNow());
        await Assert.That(area.RecordText).IsEqualTo("00:00");
        time.Advance(TimeSpan.FromSeconds(42));
        await Assert.That(area.RecordText).IsEqualTo("00:42");
        time.Advance(TimeSpan.FromMinutes(12));
        await Assert.That(area.ElapsedText).IsEqualTo("12:42");
        await area.DisposeAsync();
        await Assert.That(time.ActiveTimers).IsEqualTo(0);
    }

    [Test]
    public async Task The_REC_timer_stops_at_the_attached_duration_once_the_session_ends()
    {
        await using var harness = await ChatServerHarness.StartAsync(allowRecording: true);
        var client = await harness.ConnectClientAsync();
        await harness.WaitForSessionAsync();
        var time = new ManualTime();
        await using var documents = new DocumentsViewModel(new TestUi(), null, time);
        var area = new SessionAreaViewModel(documents);
        var live = await AttachAsync(documents, harness);

        time.Advance(TimeSpan.FromSeconds(42));
        await Assert.That(area.RecordText).IsEqualTo("00:42");
        await client.DisconnectAsync();
        await Wait.UntilAsync(() => live.State == LiveState.Ended, "the session to end");
        await Assert.That(live.EndedAt).IsEqualTo(time.GetUtcNow());

        // Minutes later the timer still reads the attached duration, as the session-ended strip does.
        time.Advance(TimeSpan.FromMinutes(5));
        await Assert.That(area.RecordText).IsEqualTo("00:42");
        await Assert.That(area.ElapsedText).IsEqualTo("00:42");
        await area.DisposeAsync();
    }

    [Test]
    public async Task A_connect_picks_its_session_in_the_popover()
    {
        await using var documents = new DocumentsViewModel(new TestUi(), null);
        var area = new SessionAreaViewModel(documents);
        await using var connect = documents.CreateConnect();
        connect.Endpoint = "tcp://10.0.4.12:7711";
        InspectorSessionInfo[] sessions =
        [
            new(0x3A21, NntlSessionRole.Server, InspectorSessionState.Connected, 0x3A21, "Tcp", "chat", "192.168.1.40:5100", 0, 0),
            new(0x3A22, NntlSessionRole.Server, InspectorSessionState.Handshaking, 0, "Tcp", "tcp", "10.0.4.88:5101", 0, 0),
        ];

        var pick = area.ChooseAsync(connect, sessions);
        await Assert.That(area.HasConnection).IsTrue();
        await Assert.That(area.HasLive).IsFalse();
        await Assert.That(area.IsPopoverOpen).IsTrue();
        await Assert.That(area.EndpointText).IsEqualTo("10.0.4.12:7711");
        await Assert.That(area.SessionText).IsEqualTo("—");
        await Assert.That(area.Sessions.Select(s => s.IdText).ToArray()).IsEquivalentTo(new[] { "0x3A21", "0x3A22" });
        await Assert.That(area.Sessions[0].Detail).IsEqualTo("192.168.1.40:5100 · Tcp · chat");
        await Assert.That(area.Sessions[1].Detail).IsEqualTo("10.0.4.88:5101 · Tcp");
        await Assert.That(area.Sessions[1].IsConnected).IsFalse();
        await Assert.That(area.ConnectedText).IsEqualTo("1 connected");

        await area.ChooseCommand.ExecuteAsync(area.Sessions[1]);
        await Assert.That((await pick)!.Value.Id).IsEqualTo(0x3A22);
        await Assert.That(area.IsPopoverOpen).IsFalse();
        await Assert.That(area.HasConnection).IsFalse();

        // Closing the popover without a pick leaves the dialog to try again.
        var dismissed = area.ChooseAsync(connect, sessions);
        area.IsPopoverOpen = false;
        await Assert.That(await dismissed).IsNull();
        await Assert.That(area.HasConnection).IsFalse();

        // The dialog canceled while the list waits: the popover and the pending connection group go too (I37).
        using var cancel = new CancellationTokenSource();
        var abandoned = area.ChooseAsync(connect, sessions, cancel.Token);
        await Assert.That(area.IsChoosing).IsTrue();
        cancel.Cancel();
        await Assert.That(await abandoned).IsNull();
        await Assert.That(area.IsChoosing).IsFalse();
        await Assert.That(area.IsPopoverOpen).IsFalse();
        await Assert.That(area.HasConnection).IsFalse();
    }

    [Test]
    public async Task Clocks_and_endpoints_read_as_the_title_bar_shows_them()
    {
        await Assert.That(SessionAreaViewModel.Clock(TimeSpan.FromSeconds(42))).IsEqualTo("00:42");
        await Assert.That(SessionAreaViewModel.Clock(TimeSpan.FromSeconds(724))).IsEqualTo("12:04");
        await Assert.That(SessionAreaViewModel.Clock(new TimeSpan(1, 2, 3))).IsEqualTo("1:02:03");
        await Assert.That(SessionAreaViewModel.Clock(TimeSpan.FromSeconds(-5))).IsEqualTo("00:00");
        await Assert.That(SessionAreaViewModel.Uptime(TimeSpan.FromSeconds(42))).IsEqualTo("0:42");
        await Assert.That(SessionAreaViewModel.Uptime(TimeSpan.FromSeconds(191))).IsEqualTo("3:11");
        await Assert.That(SessionAreaViewModel.Display(new EndpointAddress(EndpointScheme.Tls, "10.0.4.12", 7711))).IsEqualTo("10.0.4.12:7711");
        await Assert.That(SessionAreaViewModel.Display(new EndpointAddress(EndpointScheme.Tcp, "::1", 7711))).IsEqualTo("[::1]:7711");
        await Assert.That(SessionAreaViewModel.Display(new EndpointAddress(EndpointScheme.Uds, "/tmp/nexnet.sock", 0))).IsEqualTo("/tmp/nexnet.sock");
    }
}
