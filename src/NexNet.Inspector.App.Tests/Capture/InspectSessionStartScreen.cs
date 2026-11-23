using Avalonia.Controls;
using NexNet.Inspector.App.Controls.Timeline;
using NexNet.Inspector.App.Model;
using NexNet.Inspector.App.Services;
using NexNet.Inspector.App.Tests.Documents;
using NexNet.Inspector.App.ViewModels;
using NexNet.Inspector.App.Views;
using NexNet.Inspector.Core.Assembly;
using NexNet.Inspector.Core.Model;
using NexNet.Inspector.Core.Session;

namespace NexNet.Inspector.App.Tests.Capture;

/// <summary>
/// The inspect-session-start screen (PLAN §8) from the Chat fixture chat-server.nntl, recorded server side from the
/// connection's first byte: the first 50 ms (preamble, greetings, the Users collection, Join, the canceled SendMessage,
/// pings), Events on the left and the Hex stream on the right, the ClientGreeting selected so the handshake is traced
/// (band over the HANDSHAKE lane, Events rows 1–4 tinted, the Handshake tab and its Pre / Greet assembly bar) and the
/// handshake's Session lane pinned open. The title bar's "ALL · 3" record-all badge and the session popover are I28's.
/// </summary>
public static class InspectSessionStartScreen
{
    /// <summary>The window: the connection's first 50 ms.</summary>
    public const double WindowMilliseconds = 50;

    /// <summary>What the scenario picked from the fixture, for tests.</summary>
    public sealed record Picks(FileDocumentViewModel Document, FrameKey Greeting, TimeWindow Window);

    public static Window Create(CaptureContext context)
    {
        MainWindowViewModel viewModel = new(context.Themes, store: null, new DocumentsViewModel(AvaloniaUiDispatcher.Instance, null));
        Open(viewModel);
        MainWindow window = new() { DataContext = viewModel };
        window.Inspect.Timeline.Layout.Animator.Enabled = false;
        return window;
    }

    /// <summary>Opens the fixture into <paramref name="viewModel"/> and applies the screen's state. Pumps the UI thread while it loads.</summary>
    public static Picks Open(MainWindowViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        DocumentsViewModel documents = viewModel.Documents;
        Task<FileDocumentViewModel?> opening = documents.OpenRecordingAsync(Wait.Fixture("chat-server.nntl"));
        InspectLiveScreen.Pump(() => opening.IsCompleted);
        FileDocumentViewModel document = opening.Result ?? throw new InvalidOperationException("the fixture did not open");
        InspectLiveScreen.Pump(() => document.Indexing.IsCompleted && document.IndexingState == IndexingState.Complete);

        Picks picks = Pick(document);
        viewModel.Inspect.Left.View.Value = StreamPaneKind.Events;
        viewModel.Inspect.Right.View.Value = StreamPaneKind.Hex;
        SyncBus sync = documents.Sync;
        sync.SetWindow(picks.Window, byUser: false);
        long ticks;
        CaptureSession session = document.Session!;
        lock (session.Gate) ticks = session.TryGetFrame(picks.Greeting, out Core.Framing.FrameRecord frame) ? frame.StartTicks : session.StartTicks;
        sync.Select(Selection.OfFrame(picks.Greeting), ticks);
        viewModel.Inspect.Timeline.Model.SetPinned(new LaneKey(LaneKind.Session, 0), true);
        InspectLiveScreen.Pump(static () => true);
        return picks;
    }

    /// <summary>Finds the ClientGreeting and the first 50 ms in an indexed fixture document.</summary>
    public static Picks Pick(FileDocumentViewModel document)
    {
        ArgumentNullException.ThrowIfNull(document);
        CaptureSession session = document.Session!;
        lock (session.Gate)
        {
            FrameKey greeting = default;
            bool found = false;
            foreach (GreetingRow row in session.Assembler.Greetings)
            {
                if (row.Kind != GreetingKind.Client) continue;
                greeting = new FrameKey(row.Direction, row.Seq);
                found = true;
                break;
            }

            if (!found) throw new InvalidOperationException("the fixture has no ClientGreeting");
            TimeWindow window = TimeWindow.FromDuration(session.StartTicks,
                TimeWindow.TicksFromMilliseconds(WindowMilliseconds, session.Header.StopwatchFrequency));
            return new Picks(document, greeting, window);
        }
    }
}
