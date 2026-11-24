using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using Avalonia.Threading;
using NexNet.Inspector.App.Controls.Compare;
using NexNet.Inspector.App.Model;
using NexNet.Inspector.App.Services;
using NexNet.Inspector.App.Tests.Documents;
using NexNet.Inspector.App.ViewModels;
using NexNet.Inspector.Core.Compare;
using NexNet.Inspector.Core.Format;
using NexNet.Inspector.Core.Model;
using NexNet.Inspector.Core.Session;
using NexNet.Messages;

namespace NexNet.Inspector.App.Tests.Compare;

/// <summary>
/// The Compare workspace builds, refreshes and re-aligns off the UI thread, and builds the selected entity's field diff
/// there too: the UI thread keeps running while a slow compare of a large pair builds (the workspace says "Comparing…"),
/// a re-alignment keeps the previous result on screen, and the latest request wins — superseded work is cancelled and its
/// result never lands.
/// </summary>
[NotInParallel("avalonia")]
public sealed class CompareBackgroundTests
{
    // Long enough that work blocked on the UI thread would show in the timings; cancelled work stops waiting at once.
    private static readonly TimeSpan Blocked = TimeSpan.FromSeconds(10);

    [Test]
    public async Task A_slow_build_of_a_large_pair_leaves_the_UI_thread_free_and_a_superseded_build_is_cancelled()
    {
        using TempDirectory temp = new();
        string pathA = LargePair.Write(temp, "large-client.nntl", NntlSessionRole.Client, extraOps: 0);
        string pathB = LargePair.Write(temp, "large-server.nntl", NntlSessionRole.Server, extraOps: 2);
        SwitchableUi ui = new(new TestUi());
        await using DocumentsViewModel documents = new(ui, null);
        ShellChromeViewModel chrome = new(documents);
        using CompareWorkspaceViewModel compare = new(documents, chrome);
        FileDocumentViewModel a = await CompareFixture.OpenIndexedAsync(documents, pathA);
        FileDocumentViewModel b = await CompareFixture.OpenIndexedAsync(documents, pathB);
        FileDocumentViewModel other = await CompareFixture.OpenIndexedAsync(documents, "chat-server.nntl");

        ConcurrentQueue<CancellationToken> builds = new();
        using SemaphoreSlim entered = new(0);
        using ManualResetEventSlim release = new();
        compare.WorkHook = (work, token) =>
        {
            if (work != CompareWork.Build) return;
            builds.Enqueue(token);
            entered.Release();
            release.Wait(Blocked, token);
        };
        List<CaptureSession> published = [];
        compare.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(CompareWorkspaceViewModel.Session) && compare.Session is { } session) published.Add(session.B);
        };

        Probe probe = await HeadlessSession.Run(() =>
        {
            ui.Target = AvaloniaUiDispatcher.Instance;
            try
            {
                a.IsIncludedInCompare = true;
                other.IsIncludedInCompare = true;
                Stopwatch clock = Stopwatch.StartNew();
                chrome.Workspace.Value = Workspace.Compare;
                TimeSpan returned = clock.Elapsed;
                bool firstStarted = entered.Wait(Blocked);

                // The UI thread goes on while the build waits on its worker.
                bool ran = false;
                Dispatcher.UIThread.Post(() => ran = true);
                Dispatcher.UIThread.RunJobs();
                (bool computing, bool hasSession, string empty) = (compare.IsComputing, compare.HasSession, compare.EmptyText);

                // The user changes the pair before the first compare lands: that build is cancelled, the new one wins.
                other.IsIncludedInCompare = false;
                b.IsIncludedInCompare = true;
                bool secondStarted = entered.Wait(Blocked);
                builds.TryPeek(out CancellationToken first);
                bool firstCancelled = first.IsCancellationRequested;
                release.Set();
                Task computed = compare.WhenComputedAsync();
                Capture.InspectLiveScreen.Pump(() => computed.IsCompleted);

                CompareSession? session = compare.Session;
                return new Probe(returned, firstStarted && secondStarted, ran, computing, hasSession, empty, firstCancelled, builds.Count,
                    session?.A == a.Session && session?.B == b.Session, compare.IsComputing, compare.Rows.DensityA.Total(CompareCategory.Transport),
                    compare.DeltaCount);
            }
            finally
            {
                compare.WorkHook = null;
                ui.Target = new TestUi();
            }
        });

        using (Assert.Multiple())
        {
            await Assert.That(probe.Returned).IsLessThan(TimeSpan.FromSeconds(2)).Because("the build runs on the thread pool");
            await Assert.That(probe.BothStarted).IsTrue();
            await Assert.That(probe.UiRan).IsTrue();
            await Assert.That(probe.ComputingMeanwhile).IsTrue();
            await Assert.That(probe.SessionMeanwhile).IsFalse();
            await Assert.That(probe.EmptyMeanwhile).IsEqualTo(CompareWorkspaceViewModel.ComparingText);
            await Assert.That(probe.FirstCancelled).IsTrue();
            await Assert.That(probe.Builds).IsEqualTo(2);
            await Assert.That(probe.LatestPair).IsTrue();
            await Assert.That(probe.ComputingAfter).IsFalse();
            await Assert.That(probe.Frames).IsGreaterThan(LargePair.FramesPerDirection);
            await Assert.That(probe.Deltas).IsGreaterThan(0);
            // The superseded pair's compare never landed.
            await Assert.That(published.All(s => s == b.Session)).IsTrue();
        }
    }

    [Test]
    public async Task A_re_alignment_keeps_the_compare_on_screen_and_the_latest_alignment_wins()
    {
        await using CompareFixture f = await CompareFixture.OpenAsync();
        CompareSession session = f.Compare.Session!;
        ConcurrentQueue<CancellationToken> aligns = new();
        using SemaphoreSlim entered = new(0);
        f.Compare.WorkHook = (work, token) =>
        {
            if (work != CompareWork.Align) return;
            aligns.Enqueue(token);
            entered.Release();
            // Only the first is slow: the second supersedes it.
            if (aligns.Count == 1) Task.Delay(Blocked, token).Wait(token);
        };

        f.Compare.Align.Value = Alignment.Relative;
        await Assert.That(await entered.WaitAsync(Blocked)).IsTrue();
        using (Assert.Multiple())
        {
            await Assert.That(f.Compare.IsComputing).IsTrue();
            await Assert.That(f.Compare.Session).IsSameReferenceAs(session).Because("the previous result stays visible");
            await Assert.That(session.Alignment).IsEqualTo(Alignment.OnEvent);
        }

        f.Compare.Align.Value = Alignment.WallClock;
        await f.Compare.WhenComputedAsync();
        aligns.TryPeek(out CancellationToken first);
        using (Assert.Multiple())
        {
            await Assert.That(first.IsCancellationRequested).IsTrue();
            await Assert.That(aligns.Count).IsEqualTo(2);
            await Assert.That(f.Compare.Session).IsSameReferenceAs(session);
            await Assert.That(session.Alignment).IsEqualTo(Alignment.WallClock);
            await Assert.That(f.Compare.IsComputing).IsFalse();
            await Assert.That(f.Compare.DeltaCount).IsEqualTo(session.Deltas.Count);
        }
    }

    [Test]
    public async Task A_slow_field_diff_does_not_hold_the_selection_and_a_newer_pick_wins()
    {
        await using CompareFixture f = await CompareFixture.OpenAsync();
        CompareSession session = f.Compare.Session!;
        int message = Capture.CompareScreen.FindInvocation(session, "SendMessage");
        int attachment = Capture.CompareScreen.FindInvocation(session, "SendAttachment");
        ConcurrentQueue<CancellationToken> diffs = new();
        using SemaphoreSlim entered = new(0);
        f.Compare.WorkHook = (work, token) =>
        {
            if (work != CompareWork.FieldDiff) return;
            diffs.Enqueue(token);
            entered.Release();
            if (diffs.Count == 1) Task.Delay(Blocked, token).Wait(token);
        };

        Stopwatch clock = Stopwatch.StartNew();
        f.Compare.Select(message);
        TimeSpan returned = clock.Elapsed;
        await Assert.That(await entered.WaitAsync(Blocked)).IsTrue();
        FieldDiffViewModel fields = f.Compare.DetailPanel.FieldDiff;
        using (Assert.Multiple())
        {
            await Assert.That(returned).IsLessThan(TimeSpan.FromSeconds(2));
            await Assert.That(f.Compare.Detail?.Index).IsEqualTo(message);
            await Assert.That(fields.Match).IsEqualTo(-1).Because("the diff has not landed");
        }

        f.Compare.Select(attachment);
        await f.Compare.WhenComputedAsync();
        diffs.TryPeek(out CancellationToken first);
        using (Assert.Multiple())
        {
            await Assert.That(first.IsCancellationRequested).IsTrue();
            await Assert.That(fields.Match).IsEqualTo(attachment);
            await Assert.That(fields.Compare).IsSameReferenceAs(session);
            await Assert.That(f.Compare.DetailPanel.Hex.HasBytes).IsTrue();
        }
    }

    private sealed record Probe(TimeSpan Returned, bool BothStarted, bool UiRan, bool ComputingMeanwhile, bool SessionMeanwhile,
        string EmptyMeanwhile, bool FirstCancelled, int Builds, bool LatestPair, bool ComputingAfter, int Frames, int Deltas);

    /// <summary>
    /// A large synthetic pair: one connection's client side and server side, a preamble and then hundreds of thousands
    /// of Ping frames in both directions (the server side a few more), so building the compare walks every frame.
    /// </summary>
    private static class LargePair
    {
        public const int FramesPerOp = 256;
        public const int OpsPerDirection = 400;
        public const int FramesPerDirection = FramesPerOp * OpsPerDirection;
        private const long SessionId = 0x3A21;

        public static string Write(TempDirectory directory, string name, NntlSessionRole role, int extraOps)
        {
            string path = directory.File(name);
            NntlFileHeader header = new(NntlFormat.Version, new DateTime(2026, 11, 20, 14, 2, 0, DateTimeKind.Utc).Ticks, 10_000_000, 0,
                "inspector-app-tests", "1.0.0", null);
            using FileStream stream = File.Create(path);
            using NntlWriter writer = new(stream, header);
            writer.WriteSessionOpen(new NntlSessionOpen(SessionId, role, 0, "Tcp", null, "127.0.0.1:7711", "127.0.0.1:50000", ReadOnlyMemory<byte>.Empty));

            byte[] pings = new byte[FramesPerOp];
            Array.Fill(pings, (byte)MessageType.Ping);
            // The client sends the preamble: a client capture's outbound, a server capture's inbound.
            TapDirection fromClient = role == NntlSessionRole.Client ? TapDirection.Outbound : TapDirection.Inbound;
            long opId = 0, ticks = 1_000;
            writer.WriteTransportOp(SessionId, fromClient, opId++, ticks, [0x4E, 0x6E, 0x50, 0x14, 0, 0, 0, 1]);
            for (int i = 0; i < (2 * OpsPerDirection) + extraOps; i++)
            {
                ticks += 10_000;
                TapDirection direction = i % 2 == 0 ? fromClient : (TapDirection)(1 - (int)fromClient);
                writer.WriteTransportOp(SessionId, direction, opId++, ticks, pings);
            }

            return path;
        }
    }
}
