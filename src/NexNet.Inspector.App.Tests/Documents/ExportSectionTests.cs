using NexNet.Inspector.App.Model;
using NexNet.Inspector.App.Services;
using NexNet.Inspector.App.Settings;
using NexNet.Inspector.App.Tests.Inspector;
using NexNet.Inspector.App.ViewModels;
using NexNet.Inspector.Core.Assembly;
using NexNet.Inspector.Core.Format;
using NexNet.Inspector.Core.Framing;
using NexNet.Inspector.Core.Model;
using NexNet.Inspector.Core.Schema;
using NexNet.Inspector.Core.Session;
using NexNet.Inspector.Sample;
using NexNet.Inspector.Tap;
using NexNet.Messages;

namespace NexNet.Inspector.App.Tests.Documents;

/// <summary>
/// Export section as .nntl… (I40): the dialog writes the time window, the selected message's span or a typed range of
/// a file or a live document to a recording that opens in the app as a file document, holds the same frames in that
/// range, decodes them by its embedded schema and is named after the nexus, the session and the range.
/// </summary>
public class ExportSectionTests
{
    [Test]
    public async Task The_time_window_of_a_recording_exports_and_reopens_as_a_file_document_with_the_same_frames()
    {
        using var temp = new TempDirectory();
        var store = new SettingsStore(temp.File("settings.json"));
        await using var documents = new DocumentsViewModel(new TestUi(), store);
        var (document, picks) = await ChatFixture.OpenAsync(documents);
        documents.Sync.SetWindow(picks.Window, byUser: false);
        string? asked = null;
        documents.SavePicker = name =>
        {
            asked = name;
            return Task.FromResult<string?>(temp.File(name));
        };

        var dialog = new ExportSectionDialogViewModel(documents, document);
        await Assert.That(dialog.Range).IsEqualTo(ExportRangeKind.Window);
        await Assert.That(dialog.IncludeHandshake).IsTrue();
        await Assert.That(dialog.IncludeSchema).IsTrue();
        await Assert.That(dialog.KeepRedaction).IsTrue();
        await Assert.That(dialog.SuggestedName).Matches("^chat-0x[0-9A-F]{4}-[0-9]+\\.[0-9]-[0-9]+\\.[0-9]s\\.nntl$");

        string? closed = null;
        dialog.Closed += d => closed = d.Result.Result;
        await dialog.ExportCommand.ExecuteAsync(null);
        await Assert.That(dialog.Error).IsNull();
        await Assert.That(asked).IsEqualTo(dialog.SuggestedName);
        var path = await dialog.Result;
        await Assert.That(path).IsEqualTo(Path.GetFullPath(temp.File(asked!)));
        await Assert.That(closed).IsEqualTo(path);
        await Assert.That(store.Current.RecentFiles[0].Path).IsEqualTo(path);

        // It opens in the app as a File document, with the window's frames (and the handshake) decoded by its schema.
        var exported = (await documents.OpenRecordingAsync(path!))!;
        await exported.Indexing;
        await Wait.UntilAsync(() => exported.IndexingState == IndexingState.Complete, "the export to index");
        var copy = exported.Session!;
        var source = document.Session!;
        await Assert.That(copy.Schema.Source).IsEqualTo(SchemaSource.Embedded);
        await Assert.That(copy.Assembler.Greetings.Length).IsEqualTo(source.Assembler.Greetings.Length);
        foreach (var direction in new[] { TapDirection.Inbound, TapDirection.Outbound })
        {
            var want = Frames(source, direction, picks.Window.Start, picks.Window.End);
            var got = Frames(copy, direction, picks.Window.Start, long.MaxValue);
            await Assert.That(got.SequenceEqual(want)).IsTrue().Because(direction.ToString());
        }

        await Assert.That(Methods(copy)).IsEqualTo(Methods(source, picks.Window.Start, picks.Window.End));
    }

    [Test]
    public async Task The_selected_message_and_a_typed_range_choose_what_is_exported()
    {
        using var temp = new TempDirectory();
        await using var documents = new DocumentsViewModel(new TestUi(), null);
        var (document, picks) = await ChatFixture.OpenAsync(documents);
        documents.SavePicker = name => Task.FromResult<string?>(temp.File(name));

        // Nothing selected: the message and trace radios are off and asking for them falls back to the window.
        var none = new ExportSectionDialogViewModel(documents, document, ExportRangeKind.Message);
        await Assert.That(none.HasMessage).IsFalse();
        await Assert.That(none.Range).IsEqualTo(ExportRangeKind.Window);
        none.IsMessage = true;
        await Assert.That(none.Range).IsEqualTo(ExportRangeKind.Window);

        // The selected SendAttachment: its frames, first to last.
        var session = document.Session!;
        session.TryGetFrame(picks.Chunk, out var chunk);
        documents.Sync.Select(Selection.OfMessage(picks.Message, picks.Chunk), chunk.StartTicks);
        var dialog = new ExportSectionDialogViewModel(documents, document, ExportRangeKind.Message);
        await Assert.That(dialog.HasMessage).IsTrue();
        await Assert.That(dialog.Range).IsEqualTo(ExportRangeKind.Message);
        await Assert.That(dialog.TryGetRange(out var from, out var to, out _)).IsTrue();
        var trace = MessageTrace.Resolve(session, documents.Sync.Selection);
        await Assert.That((from, to)).IsEqualTo((trace.Start, trace.End));

        var path = temp.File("message.nntl");
        var result = await dialog.ExportToAsync(path, new CaptureExportRange(from, to));
        await Assert.That(result.Frames).IsGreaterThanOrEqualTo(trace.MemberCount);
        using (var recording = RecordingFile.Open(path))
        {
            var copy = recording.CreateSession(recording.Sessions[0].SessionId);
            recording.Index(copy);
            await Assert.That(Methods(copy)).Contains("SendAttachment");
        }

        // A typed range: ms from the capture's start, as the time column writes them; bad text says so.
        dialog.IsTyped = true;
        dialog.FromText = "abc";
        await dialog.ExportCommand.ExecuteAsync(null);
        await Assert.That(dialog.Error).IsNotNull();
        await Assert.That(dialog.Result.IsCompleted).IsFalse();
        dialog.FromText = "1,000.5";
        dialog.ToText = "0";
        await Assert.That(dialog.Error).IsNull();
        await Assert.That(dialog.TryGetRange(out var a, out var b, out _)).IsTrue();
        await Assert.That(a).IsEqualTo(session.StartTicks);
        await Assert.That(b).IsEqualTo(session.StartTicks + TimeWindow.TicksFromMilliseconds(1000.5, document.Frequency));
        dialog.CancelCommand.Execute(null);
        await Assert.That(await dialog.Result).IsNull();
    }

    [Test]
    public async Task A_live_document_exports_what_it_captured()
    {
        await using var harness = await ChatServerHarness.StartAsync(allowRecording: true);
        using var temp = new TempDirectory();
        await using var documents = new DocumentsViewModel(new TestUi(), null);
        documents.SavePicker = name => Task.FromResult<string?>(temp.File(name));
        var client = await harness.ConnectClientAsync();
        var info = await harness.WaitForSessionAsync();
        EndpointAddress.TryParse(harness.EndpointAddress, out var address, out _);
        var connection = await InspectorConnection.ConnectAsync(address, "app-test-endpoint-token"u8.ToArray(), EndpointConnectOptions.Default);
        var live = await documents.AttachAsync(connection, info, InspectorOverflow.Block, snapshot: true);
        await client.Proxy.Join("ada");
        await client.Proxy.SendMessage(new ChatMessage { Room = "general", Author = "ada", Text = "hello" }, CancellationToken.None);
        await Wait.UntilAsync(() => live.Session is { } s && Invocations(s) >= 2, "the conversation");

        var dialog = new ExportSectionDialogViewModel(documents, live) { IsTyped = true };
        dialog.FromText = "0";
        dialog.ToText = "100000000";
        await Assert.That(dialog.SuggestedName).StartsWith("chat-" + LiveDocumentViewModel.SessionHex(info.Id) + "-0.0-");
        await dialog.ExportCommand.ExecuteAsync(null);
        await Assert.That(dialog.Error).IsNull();
        var path = await dialog.Result;
        await Assert.That(path).IsNotNull();

        var captured = live.Session!;
        int frames;
        lock (captured.Gate)
            frames = captured.FrameCount;
        using var recording = RecordingFile.Open(path!);
        var copy = recording.CreateSession(recording.Sessions[0].SessionId);
        recording.Index(copy);
        await Assert.That(copy.FrameCount).IsGreaterThanOrEqualTo(frames);
        await Assert.That(Methods(copy)).Contains("SendMessage");
        await Assert.That(copy.Close!.Value.Kind).IsEqualTo(NntlCloseKind.RecordingStopped);
    }

    private static string[] Frames(CaptureSession session, TapDirection direction, long from, long to)
    {
        lock (session.Gate)
        {
            return session.Assembler.GetFrames(direction).ToArray()
                .Where(f => f.StartTicks >= from && f.StartTicks <= to && f.Kind != FrameKind.Preamble
                            && f.Type is not (MessageType.ClientGreeting or MessageType.ServerGreeting or MessageType.ClientGreetingReconnection))
                .Select(f =>
                {
                    var bytes = new byte[f.Length];
                    session.GetStore(direction).TryCopyTo(f.Offset, bytes);
                    return f.Type + " " + f.Length + " " + f.StartTicks + " " + Convert.ToHexString(bytes);
                }).ToArray();
        }
    }

    private static string Methods(CaptureSession session, long from = long.MinValue, long to = long.MaxValue)
    {
        var names = new List<string>();
        lock (session.Gate)
        {
            foreach (var invocation in session.Assembler.Invocations)
            {
                if (!invocation.HasRequest || !session.TryGetFrame(new FrameKey(invocation.Request.Direction, invocation.Request.Seq), out var frame)
                    || frame.StartTicks < from || frame.StartTicks > to)
                    continue;
                if (session.Decoder.TryGetMethod(invocation, out var method))
                    names.Add(method.Name);
            }
        }

        return string.Join(",", names);
    }

    private static int Invocations(CaptureSession session)
    {
        lock (session.Gate)
            return session.Assembler.Invocations.Length;
    }
}
