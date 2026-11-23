using Avalonia.Controls;
using Avalonia.Threading;
using NexNet.Inspector.App.Controls.Timeline;
using NexNet.Inspector.App.Model;
using NexNet.Inspector.App.Services;
using NexNet.Inspector.App.Tests.Documents;
using NexNet.Inspector.App.ViewModels;
using NexNet.Inspector.App.Views;
using NexNet.Inspector.Core.Assembly;
using NexNet.Inspector.Core.Model;
using NexNet.Inspector.Core.Schema;
using NexNet.Inspector.Core.Session;

namespace NexNet.Inspector.App.Tests.Capture;

/// <summary>
/// The inspect-live screen (PLAN §8) from the Chat fixture chat-server.nntl: the window from just before the five-chunk
/// SendAttachment starts to just after the dropped-events gap, so the whole traced message and the hatched gap show
/// (the package's 1,240–1,290 ms window holds both only in its mock data: in the fixture the chunks are paced ~260 ms
/// apart and the gap follows the last one); SendAttachment's chunk 2 selected (traced on the timeline, decoded in the
/// inspector with its 5-chunk assembly bar) and the playhead on it; the interleaved FileUpload pipe pinned.
/// </summary>
public static class InspectLiveScreen
{
    /// <summary>What the scenario picked from the fixture, for tests.</summary>
    public sealed record Picks(FileDocumentViewModel Document, MessageRef Message, FrameKey Chunk, int Pipe, TimeWindow Window);

    public static Window Create(CaptureContext context)
    {
        MainWindowViewModel viewModel = new(context.Themes, store: null, new DocumentsViewModel(AvaloniaUiDispatcher.Instance, null));
        Open(viewModel);
        MainWindow window = new() { DataContext = viewModel };
        // A still frame: lanes at their final heights, not mid-animation.
        window.Inspect.Timeline.Layout.Animator.Enabled = false;
        return window;
    }

    /// <summary>Opens the fixture into <paramref name="viewModel"/> and applies the screen's state. Pumps the UI thread while it loads.</summary>
    public static Picks Open(MainWindowViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        DocumentsViewModel documents = viewModel.Documents;
        Task<FileDocumentViewModel?> opening = documents.OpenRecordingAsync(Wait.Fixture("chat-server.nntl"));
        Pump(() => opening.IsCompleted);
        FileDocumentViewModel document = opening.Result ?? throw new InvalidOperationException("the fixture did not open");
        Pump(() => document.Indexing.IsCompleted && document.IndexingState == IndexingState.Complete);

        Picks picks = Pick(document);
        SyncBus sync = documents.Sync;
        sync.SetWindow(picks.Window, byUser: false);
        sync.Select(Selection.OfMessage(picks.Message, picks.Chunk), TicksOf(document.Session!, picks.Chunk));
        viewModel.Inspect.Timeline.Model.SetPinned(new LaneKey(LaneKind.Pipe, picks.Pipe), true);
        Pump(static () => true);
        return picks;
    }

    /// <summary>Finds the screen's subjects in an indexed fixture document.</summary>
    public static Picks Pick(FileDocumentViewModel document)
    {
        CaptureSession session = document.Session!;
        lock (session.Gate)
        {
            MessageAssembler assembler = session.Assembler;
            int attachment = -1;
            for (int i = 0; i < assembler.Invocations.Length; i++)
            {
                if (session.Decoder.TryGetMethod(assembler.Invocations[i], out SchemaMethod? method) && method.Name == "SendAttachment")
                {
                    attachment = i;
                    break;
                }
            }

            if (attachment < 0) throw new InvalidOperationException("the fixture has no SendAttachment");
            ArgumentNullException.ThrowIfNull(document);
            InvocationRow invocation = assembler.Invocations[attachment];
            MessageRef message = invocation.Request;
            List<TraceFragment> fragments = [];
            assembler.GetFragments(message, fragments);
            // Chunk 2: the third fragment (after MessageStart and chunk 1).
            TraceFragment chunk = fragments[2];
            List<Core.Framing.FrameRecord> interleaved = [];
            assembler.GetInterleaved(message, interleaved);
            int pipe = -1;
            foreach (Core.Framing.FrameRecord frame in interleaved)
            {
                if (assembler.GetLink(frame.Direction, frame.Seq) is { Entity: EntityKind.Pipe } link)
                {
                    pipe = link.EntityIndex;
                    break;
                }
            }

            long ms = session.Header.StopwatchFrequency / 1000;
            assembler.TryGetFrame(message.Direction, message.Seq, out Core.Framing.FrameRecord start);
            long gapEnd = start.StartTicks;
            foreach (Core.Format.NntlGap gap in session.Gaps) gapEnd = Math.Max(gapEnd, gap.ToTicks);
            TimeWindow window = new(start.StartTicks - (10 * ms), gapEnd + (25 * ms));
            return new Picks(document, message, new FrameKey(chunk.Direction, chunk.Seq), pipe, window);
        }
    }

    private static long TicksOf(CaptureSession session, FrameKey key)
    {
        lock (session.Gate) return session.TryGetFrame(key, out Core.Framing.FrameRecord frame) ? frame.StartTicks : session.StartTicks;
    }

    // The recording opens and indexes on background threads that post back to the UI thread: run its jobs until done.
    private static void Pump(Func<bool> done)
    {
        long deadline = Environment.TickCount64 + 30_000;
        do
        {
            Dispatcher.UIThread.RunJobs();
            if (done()) break;
            Thread.Sleep(5);
        }
        while (Environment.TickCount64 < deadline);

        Dispatcher.UIThread.RunJobs();
        if (!done()) throw new TimeoutException("the fixture did not load");
    }
}
