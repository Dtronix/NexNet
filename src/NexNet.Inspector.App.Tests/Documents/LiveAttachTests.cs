using NexNet.Inspector.App.Model;
using NexNet.Inspector.App.Services;
using NexNet.Inspector.App.Settings;
using NexNet.Inspector.App.ViewModels;
using NexNet.Inspector.Core.Assembly;
using NexNet.Inspector.Core.Model;
using NexNet.Inspector.Core.Session;
using NexNet.Inspector.Sample;
using NexNet.Inspector.Tap;
using NexNet.Messages;

namespace NexNet.Inspector.App.Tests.Documents;

/// <summary>The Connect flow against a live tapped Chat server: probe, connect, attach, live frames, end, detach.</summary>
public class LiveAttachTests
{
    [Test]
    public async Task Probe_reports_policy_refusal_and_bad_addresses()
    {
        await using var harness = await ChatServerHarness.StartAsync(allowRecording: false);
        var options = new EndpointConnectOptions(TimeSpan.FromSeconds(5));

        var reached = await InspectorConnection.ProbeAsync(harness.EndpointAddress, EndpointScheme.Tcp, "app-test-endpoint-token"u8.ToArray(), options);
        await Assert.That(reached.State).IsEqualTo(ProbeState.Reached);
        await Assert.That(reached.Info!.AllowRecording).IsFalse();
        await Assert.That(reached.Message).Contains("recording disabled");
        await Assert.That(reached.Message).Contains("secrets redacted");
        await Assert.That(reached.Message).Contains("schema provided");

        var refused = await InspectorConnection.ProbeAsync(harness.EndpointAddress, EndpointScheme.Tcp, "wrong"u8.ToArray(), options);
        await Assert.That(refused.State).IsEqualTo(ProbeState.Refused);

        var invalid = await InspectorConnection.ProbeAsync("ftp://nowhere", EndpointScheme.Tcp, default, options);
        await Assert.That(invalid.State).IsEqualTo(ProbeState.Invalid);

        // host:port without a scheme takes the Transport select's.
        await Assert.That(EndpointAddress.TryParse("127.0.0.1:7711", EndpointScheme.Tls, out var tls, out _)).IsTrue();
        await Assert.That(tls.ToString()).IsEqualTo("tls://127.0.0.1:7711");
    }

    [Test]
    public async Task Typing_probes_after_a_pause_and_the_status_line_shows_the_policy()
    {
        await using var harness = await ChatServerHarness.StartAsync(allowRecording: true);
        var ui = new TestUi();
        await using var documents = new DocumentsViewModel(ui, null);
        await using var connect = documents.CreateConnect();
        connect.ProbeDelay = TimeSpan.FromMilliseconds(100);

        connect.Token = ChatServerHarness.Token;
        connect.Endpoint = harness.EndpointAddress[..^1];
        connect.Endpoint = harness.EndpointAddress;
        await Assert.That(connect.ProbeState).IsEqualTo(ProbeState.Pending);
        await Wait.UntilAsync(() => connect.ProbeState == ProbeState.Reached, "the probe");
        await Assert.That(connect.RecordingAllowed == true).IsTrue();
        await Assert.That(connect.ProbeMessage).StartsWith("Reached " + harness.EndpointAddress);

        connect.Endpoint = "";
        await Assert.That(connect.ProbeState).IsEqualTo(ProbeState.Idle);
    }

    [Test]
    public async Task Attach_streams_frames_the_window_follows_and_the_session_end_keeps_the_data()
    {
        await using var harness = await ChatServerHarness.StartAsync(allowRecording: false);
        using var temp = new TempDirectory();
        var store = new SettingsStore(temp.File("settings.json"));
        var ui = new TestUi();
        await using var documents = new DocumentsViewModel(ui, store);

        var client = await harness.ConnectClientAsync();
        await harness.WaitForSessionAsync();

        await using var connect = documents.CreateConnect();
        connect.Endpoint = harness.EndpointAddress;
        connect.Token = ChatServerHarness.Token;
        connect.Overflow = InspectorOverflow.Block;
        connect.Snapshot = true;
        await connect.ConnectCommand.ExecuteAsync(null);
        await Assert.That(connect.ConnectState).IsEqualTo(ConnectState.Connected).Because(connect.ConnectError ?? "");
        var session = connect.Sessions.Single();
        await connect.AttachCommand.ExecuteAsync(session);

        var document = connect.Document!;
        await Assert.That(document).IsNotNull().Because(connect.ConnectError ?? "");
        await Assert.That(documents.Active).IsSameReferenceAs(document);
        await Assert.That(document.State).IsEqualTo(LiveState.Live);
        await Assert.That(document.Title).IsEqualTo(LiveDocumentViewModel.TabTitle(session.Id));
        await Assert.That(document.RecordingAllowed).IsFalse();
        await Assert.That(document.RecordingDisabledTooltip).IsEqualTo("Recording disabled by server config");

        // Traffic after the attach arrives as frames, decoded with the endpoint's schema.
        var user = await client.Proxy.Join("ada");
        await client.Proxy.SendMessage(new ChatMessage { Room = "general", Author = "ada", Text = "hello" }, CancellationToken.None);
        await Wait.UntilAsync(() => document.Session is { } s && Invocations(s) >= 2, "two invocations");
        var captured = document.Session!;
        await Assert.That(captured.Source).IsEqualTo(CaptureSource.Live);
        await Assert.That(captured.Snapshot).IsNotNull();
        await Assert.That(document.SchemaSource).IsEqualTo(SchemaSource.Embedded);
        lock (captured.Gate)
        {
            var join = captured.Assembler.Invocations[0];
            var decoded = captured.Decoder.DecodeArguments(join);
            if (decoded.Problem != Core.Schema.DecodeProblem.None)
                throw new InvalidOperationException("Join did not decode: " + decoded.Problem);
        }

        await Wait.UntilAsync(() => document.FrameCount >= 4 && document.IsDirty, "the counts to refresh");

        // The window starts on the last 50 ms and slides with the live edge once data passes its right end.
        var fifty = TimeWindow.TicksFromMilliseconds(50, document.Frequency);
        await Assert.That(documents.Sync.FollowLive).IsTrue();
        await Assert.That(documents.Sync.Window.Duration).IsEqualTo(fifty);
        var firstEnd = documents.Sync.Window.End;
        await Task.Delay(120);
        await client.Proxy.SendMessage(new ChatMessage { Room = "general", Author = "ada", Text = "later" }, CancellationToken.None);
        await Wait.UntilAsync(() => document.TimeRange.End > firstEnd && documents.Sync.Window.End == document.TimeRange.End,
            "the window to follow the live edge");
        await Assert.That(documents.Sync.Window.Duration).IsEqualTo(fifty);

        var recent = store.Current.RecentEndpoints.Single();
        await Assert.That(recent.Endpoint).IsEqualTo(harness.EndpointAddress);
        await Assert.That(recent.Target).IsEqualTo(InspectorTargetKind.Session);
        await Assert.That(store.Current.Connect.Overflow).IsEqualTo(InspectorOverflow.Block);
        await Assert.That(store.Current.Connect.Endpoint).IsEqualTo(harness.EndpointAddress);

        // The session ends: the data stays, unsaved, with the reason for the banner.
        await client.DisconnectAsync();
        await Wait.UntilAsync(() => document.State == LiveState.Ended, "the session to end");
        await Assert.That(document.EndReason).IsEqualTo(DisconnectReason.Graceful);
        await Assert.That(document.CaptureState).IsEqualTo(CaptureState.Ended);
        await Assert.That(document.IsDirty).IsTrue();
        await Assert.That(document.FrameCount).IsGreaterThan(4);
        _ = user;
    }

    [Test]
    public async Task Detach_resets_the_view_and_a_new_attach_starts_from_nothing()
    {
        await using var harness = await ChatServerHarness.StartAsync(allowRecording: true);
        var ui = new TestUi();
        await using var documents = new DocumentsViewModel(ui, null);
        var client = await harness.ConnectClientAsync();
        await client.Proxy.Join("bob");
        var session = await harness.WaitForSessionAsync();

        var connection = await InspectorConnection.ConnectAsync(
            EndpointAddress.TryParse(harness.EndpointAddress, out var address, out _) ? address : default,
            "app-test-endpoint-token"u8.ToArray(), EndpointConnectOptions.Default);
        var document = await documents.AttachAsync(connection, session, InspectorOverflow.Drop, snapshot: true);
        await Assert.That(document.RecordingAllowed).IsTrue();
        await Assert.That(document.RecordingDisabledTooltip).IsNull();

        await client.Proxy.SendMessage(new ChatMessage { Room = "r", Author = "bob", Text = "one" }, CancellationToken.None);
        await Wait.UntilAsync(() => document.FrameCount > 0 && document.IsDirty, "frames");

        await document.DetachCommand.ExecuteAsync(null);
        await Assert.That(document.State).IsEqualTo(LiveState.Detached);
        await Assert.That(document.Session).IsNull();
        await Assert.That(document.FrameCount).IsEqualTo(0);
        await Assert.That(document.IsDirty).IsFalse();

        // Attach to the same session again: only what happens from now on.
        await document.AttachAsync(new InspectorAttachRequest(InspectorTargetKind.Session, session.Id, InspectorOverflow.Drop, true, false), session);
        await client.Proxy.SendMessage(new ChatMessage { Room = "r", Author = "bob", Text = "two" }, CancellationToken.None);
        // SendMessage and the ReceiveMessage broadcast it causes.
        await Wait.UntilAsync(() => document.Session is { } s && Invocations(s) >= 2, "the second attach's frames");
        await Assert.That(document.State).IsEqualTo(LiveState.Live);
        var texts = Texts(document.Session!);
        await Assert.That(texts).Contains("two");
        await Assert.That(texts).DoesNotContain("one");

        await documents.CloseAsync(document);
        await Assert.That(documents.HasDocuments).IsFalse();
    }

    [Test]
    public async Task A_mid_session_attach_resolves_pipes_opened_before_it_from_the_snapshot()
    {
        await using var harness = await ChatServerHarness.StartAsync(allowRecording: true);
        var ui = new TestUi();
        await using var documents = new DocumentsViewModel(ui, null);
        var client = await harness.ConnectClientAsync();

        // An upload pipe opened before the attach, written to after it.
        var pipe = client.CreatePipe();
        var upload = client.Proxy.UploadFile("before.bin", pipe);
        await pipe.ReadyTask;
        var session = await harness.WaitForSessionAsync();

        EndpointAddress.TryParse(harness.EndpointAddress, out var address, out _);
        var connection = await InspectorConnection.ConnectAsync(address, "app-test-endpoint-token"u8.ToArray(), EndpointConnectOptions.Default);
        var document = await documents.AttachAsync(connection, session, InspectorOverflow.Block, snapshot: true);
        await Wait.UntilAsync(() => document.Session?.Snapshot is not null, "the snapshot");

        await pipe.Output.WriteAsync(new byte[1000]);
        await Wait.UntilAsync(() => document.Session is { } s && PipeBytes(s) >= 1000, "the pipe's bytes");
        var captured = document.Session!;
        lock (captured.Gate)
        {
            var pipes = captured.Assembler.Pipes.ToArray();
            var seeded = pipes.Single(p => p.FromSnapshot);
            if (seeded.InboundBytes < 1000)
                throw new InvalidOperationException("The write landed on a pipe the snapshot did not name.");
            if (pipes.Length != 1)
                throw new InvalidOperationException("The write opened an unknown pipe: " + pipes.Length);
        }

        await pipe.CompleteAsync();
        await upload;
    }

    // Every decoded "Text" argument of the session's invocations.
    private static List<string> Texts(CaptureSession session)
    {
        var texts = new List<string>();
        lock (session.Gate)
        {
            foreach (var invocation in session.Assembler.Invocations)
            {
                if (!invocation.HasRequest)
                    continue;
                var value = session.Decoder.DecodeArguments(invocation);
                foreach (var field in value.Fields)
                {
                    if (field.Name == "Text" && value.GetText(field) is { } text)
                        texts.Add(text);
                }
            }
        }

        return texts;
    }

    private static int Invocations(CaptureSession session)
    {
        lock (session.Gate)
            return session.Assembler.Invocations.Length;
    }

    private static long PipeBytes(CaptureSession session)
    {
        lock (session.Gate)
        {
            long total = 0;
            foreach (var pipe in session.Assembler.Pipes)
                total += pipe.InboundBytes;
            return total;
        }
    }
}
