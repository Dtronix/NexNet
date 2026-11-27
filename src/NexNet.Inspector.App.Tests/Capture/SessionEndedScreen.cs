using Avalonia.Controls;
using NexNet.Inspector.App.Model;
using NexNet.Inspector.App.Services;
using NexNet.Inspector.App.Tests.Documents;
using NexNet.Inspector.App.ViewModels;
using NexNet.Inspector.App.Views;
using NexNet.Inspector.Core.Assembly;
using NexNet.Inspector.Core.Session;

namespace NexNet.Inspector.App.Tests.Capture;

/// <summary>
/// The session-ended screen (PLAN §8): inspect-live after its session disconnected. A real in-process Chat server; the
/// live tab attached to one client's session, which joined, chatted, sent an 18 KB attachment (a chunked invocation)
/// and disconnected gracefully, so the strip under the title bar reads "Session 0x…. ended — DisconnectGraceful (21)
/// from client · … · not saved" with Save .nntl… / Attach to another session ▾ / Discard. Two recordings are open in
/// further tabs; the panes stay navigable: Events on the left, Ladder on the right, the whole session in view and the
/// attachment's request selected.
/// </summary>
public static class SessionEndedScreen
{
    public static Window Create(CaptureContext context)
    {
        MainWindowViewModel viewModel = new(context.Themes, store: null, new DocumentsViewModel(AvaloniaUiDispatcher.Instance, null));
        LiveConnection live = LiveConnection.AttachAndEnd(viewModel);
        Open(viewModel, live.Document!);
        MainWindow window = new() { DataContext = viewModel };
        window.Inspect.Timeline.Layout.Animator.Enabled = false;
        live.TearDownWith(window, viewModel);
        return window;
    }

    /// <summary>Opens the further tabs and puts the ended live document's panes in the screen's state.</summary>
    public static void Open(MainWindowViewModel viewModel, LiveDocumentViewModel document)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(document);
        DocumentsViewModel documents = viewModel.Documents;
        foreach (string name in (ReadOnlySpan<string>)["chat-client.nntl", "chat-server.nntl"])
        {
            Task<FileDocumentViewModel?> opening = documents.OpenRecordingAsync(Wait.Fixture(name));
            InspectLiveScreen.Pump(() => opening.IsCompleted);
            FileDocumentViewModel file = opening.Result ?? throw new InvalidOperationException(name + " did not open");
            InspectLiveScreen.Pump(() => file.Indexing.IsCompleted);
        }

        documents.Activate(document);
        viewModel.Inspect.Left.View.Value = StreamPaneKind.Events;
        viewModel.Inspect.Right.View.Value = StreamPaneKind.Ladder;
        SyncBus sync = documents.Sync;
        sync.SetFollowLive(false);
        sync.SetWindow(document.TimeRange, byUser: false);

        // The attachment's request, from its second chunk (as inspect-live traces it).
        CaptureSession session = document.Session!;
        lock (session.Gate)
        {
            MessageAssembler assembler = session.Assembler;
            for (int i = 0; i < assembler.Invocations.Length; i++)
            {
                if (!session.Decoder.TryGetMethod(assembler.Invocations[i], out Core.Schema.SchemaMethod? method) || method.Name != "SendAttachment") continue;
                MessageRef message = assembler.Invocations[i].Request;
                List<TraceFragment> fragments = [];
                assembler.GetFragments(message, fragments);
                TraceFragment chunk = fragments[Math.Min(2, fragments.Count - 1)];
                FrameKey key = new(chunk.Direction, chunk.Seq);
                long ticks = session.TryGetFrame(key, out Core.Framing.FrameRecord frame) ? frame.StartTicks : session.StartTicks;
                sync.Select(Selection.OfMessage(message, key), ticks);
                break;
            }
        }

        InspectLiveScreen.Pump(static () => true);
    }
}
