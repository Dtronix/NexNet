using NexNet.Inspector.App.Controls.Hex;
using NexNet.Inspector.App.Controls.Ladder;
using NexNet.Inspector.App.Model;
using NexNet.Inspector.App.Model.Palette;
using NexNet.Inspector.App.Services;
using NexNet.Inspector.App.Services.Commands;
using NexNet.Inspector.App.Tests.Compare;
using NexNet.Inspector.App.Tests.Inspector;
using NexNet.Inspector.App.ViewModels;
using NexNet.Inspector.Core.Assembly;
using NexNet.Inspector.Core.Compare;
using NexNet.Inspector.Core.Framing;
using NexNet.Inspector.Core.Model;
using NexNet.Inspector.Core.Session;

namespace NexNet.Inspector.App.Tests.Documents;

/// <summary>
/// The document scope (I43, D2): the scope stack, the sync bus's bounds and follow-live, and every pane limited to the
/// scope at the Chat fixture (Events and Ladder rows, Hex ops, Message trace steps, palette results, Export's default,
/// Compare's aligned axis).
/// </summary>
public class ScopeTests
{
    private static TimeScope Scope(long start, long end) => new(start, end, "test");

    [Test]
    public async Task The_stack_nests_cuts_to_its_parent_and_Back_returns_to_the_view_before_each_scope()
    {
        ScopeStack stack = new();
        List<ScopeChange> changes = [];
        stack.Changed += changes.Add;
        TimeWindow first = new(0, 1000), second = new(200, 400);

        await Assert.That(stack.Push(Scope(100, 900), first)).IsTrue();
        // Nested: cut to the parent.
        await Assert.That(stack.Push(Scope(50, 300), second)).IsTrue();
        await Assert.That(stack.Current).IsEqualTo(Scope(100, 300));
        await Assert.That(stack.Depth).IsEqualTo(2);
        // Outside the current scope: refused, nothing raised.
        await Assert.That(stack.Push(Scope(500, 600))).IsFalse();
        await Assert.That(stack.Push(Scope(5, 5))).IsFalse();
        await Assert.That(changes.Count).IsEqualTo(2);
        await Assert.That(changes[1]).IsEqualTo(new ScopeChange(ScopeChangeKind.Pushed, new TimeWindow(100, 300)));

        await Assert.That(stack.Pop()).IsTrue();
        await Assert.That(changes[^1]).IsEqualTo(new ScopeChange(ScopeChangeKind.Popped, second));
        await Assert.That(stack.Current).IsEqualTo(Scope(100, 900));

        stack.Push(Scope(200, 300), second);
        await Assert.That(stack.Clear()).IsTrue();
        await Assert.That(changes[^1]).IsEqualTo(new ScopeChange(ScopeChangeKind.Cleared, first));
        await Assert.That(stack.IsEmpty).IsTrue();
        await Assert.That(stack.Pop()).IsFalse();
        await Assert.That(stack.Clear()).IsFalse();
    }

    [Test]
    public async Task The_bus_bounds_are_the_scope_and_the_window_stays_inside_it()
    {
        var ui = new QueuedUi();
        var bus = new SyncBus(ui);
        bus.SetBounds(new TimeWindow(0, 10_000));
        bus.SetWindow(new TimeWindow(0, 10_000), byUser: false);
        ui.RunAll();
        List<SyncChanges> seen = [];
        bus.Changed += seen.Add;

        bus.SetScope(new TimeWindow(2000, 3000));
        ui.RunAll();
        await Assert.That(bus.Bounds).IsEqualTo(new TimeWindow(2000, 3000));
        await Assert.That(bus.CaptureBounds).IsEqualTo(new TimeWindow(0, 10_000));
        await Assert.That(bus.Window).IsEqualTo(new TimeWindow(2000, 3000));
        await Assert.That((seen[^1] & (SyncChanges.Scope | SyncChanges.Bounds | SyncChanges.Window)))
            .IsEqualTo(SyncChanges.Scope | SyncChanges.Bounds | SyncChanges.Window);

        // A zoom out stops at the scope; a pan stays inside it.
        bus.ZoomAround(2500, 8);
        await Assert.That(bus.Window).IsEqualTo(new TimeWindow(2000, 3000));
        bus.SetWindow(new TimeWindow(2900, 3100));
        await Assert.That(bus.Window).IsEqualTo(new TimeWindow(2800, 3000));

        // The capture growing under a scope does not move its bounds.
        bus.SetBounds(new TimeWindow(0, 20_000));
        await Assert.That(bus.Bounds).IsEqualTo(new TimeWindow(2000, 3000));

        bus.SetScope(default);
        ui.RunAll();
        await Assert.That(bus.Bounds).IsEqualTo(new TimeWindow(0, 20_000));
        await Assert.That(bus.IsScoped).IsFalse();
        await Assert.That((seen[^1] & SyncChanges.Scope)).IsEqualTo(SyncChanges.Scope);
    }

    [Test]
    public async Task A_live_scope_stays_fixed_while_data_arrives_and_following_resumes_when_it_is_cleared()
    {
        var bus = new SyncBus(new QueuedUi());
        bus.SetWindow(new TimeWindow(0, 100), byUser: false);
        bus.SetFollowLive(true);
        bus.OnLiveData(300);
        await Assert.That(bus.Window).IsEqualTo(new TimeWindow(200, 300));

        bus.SetScope(new TimeWindow(150, 300));
        await Assert.That(bus.FollowLive).IsFalse().Because("following is suspended while scoped");
        await Assert.That(bus.ResumesFollowLive).IsTrue();
        bus.OnLiveData(800);
        await Assert.That(bus.Window).IsEqualTo(new TimeWindow(200, 300));
        await Assert.That(bus.Scope).IsEqualTo(new TimeWindow(150, 300));
        // Turning it on while scoped waits for the scope to go.
        bus.SetFollowLive(true);
        await Assert.That(bus.FollowLive).IsFalse();

        bus.SetScope(default);
        await Assert.That(bus.FollowLive).IsTrue();
        bus.OnLiveData(900);
        await Assert.That(bus.Window).IsEqualTo(new TimeWindow(800, 900));

        // Following was off when the scope was set: clearing it leaves it off.
        bus.SetFollowLive(false);
        bus.SetScope(new TimeWindow(800, 900));
        bus.SetScope(default);
        await Assert.That(bus.FollowLive).IsFalse();
    }

    [Test]
    public async Task A_ladder_scope_is_an_index_range_that_keeps_its_end_while_rows_arrive_after_it()
    {
        LadderModel model = new();
        for (int i = 0; i < 10; i++)
            model.Add(new LadderRow(new FrameKey(TapDirection.Inbound, i), i * 100, FrameKind.Frame, NexNet.Messages.MessageType.Ping,
                FrameFlags.None, 4, LadderTarget.None, 0, LadderNote.None, -1, i, i, EntityKind.None, -1, -1, 0, FrameDetail.None, 0, false));
        int generation = model.Generation;
        model.SetScope(new TimeWindow(250, 650));
        await Assert.That(model.Count).IsEqualTo(4);
        await Assert.That(model.First).IsEqualTo(3);
        await Assert.That(model[0].Ticks).IsEqualTo(300);
        await Assert.That(model.Rows.Length).IsEqualTo(4);
        await Assert.That(model.IndexOf(new FrameKey(TapDirection.Inbound, 4))).IsEqualTo(1);
        await Assert.That(model.IndexOf(new FrameKey(TapDirection.Inbound, 8))).IsEqualTo(-1);
        await Assert.That(model.IndexAtOrAfter(0)).IsEqualTo(0);
        await Assert.That(model.IndexAtOrAfter(10_000)).IsEqualTo(4);
        await Assert.That(model.Generation).IsNotEqualTo(generation);

        model.Add(new LadderRow(new FrameKey(TapDirection.Inbound, 10), 1000, FrameKind.Frame, NexNet.Messages.MessageType.Ping,
            FrameFlags.None, 4, LadderTarget.None, 0, LadderNote.None, -1, 10, 10, EntityKind.None, -1, -1, 0, FrameDetail.None, 0, false));
        model.NotifyChanged();
        await Assert.That(model.Count).IsEqualTo(4);
        await Assert.That(model.TotalCount).IsEqualTo(11);

        model.SetScope(default);
        await Assert.That(model.Count).IsEqualTo(11);
        await Assert.That(model.IndexOf(new FrameKey(TapDirection.Inbound, 8))).IsEqualTo(8);
    }

    [Test]
    public async Task Filter_to_view_scopes_the_document_and_Back_and_clear_return_to_the_view_before()
    {
        await using var documents = new DocumentsViewModel(new TestUi(), null);
        var (document, picks) = await ChatFixture.OpenAsync(documents);
        TimeWindow whole = documents.Sync.Window;
        documents.Sync.SetWindow(picks.Window, byUser: false);

        await Assert.That(documents.FilterToView()).IsTrue();
        await Assert.That(document.Scope?.Window).IsEqualTo(picks.Window);
        await Assert.That(document.Scope?.Label).IsEqualTo(ScopeLabels.View);
        await Assert.That(documents.Sync.Scope).IsEqualTo(picks.Window);
        await Assert.That(documents.Sync.Bounds).IsEqualTo(picks.Window);

        // A nested scope: the middle half of the view.
        TimeWindow inner = new(picks.Window.Start + (picks.Window.Duration / 4), picks.Window.End - (picks.Window.Duration / 4));
        TimeWindow zoomed = new(inner.Start, inner.Start + (inner.Duration / 2));
        documents.Sync.SetWindow(picks.Window, byUser: false);
        await Assert.That(documents.PushScope(TimeScope.Of(inner, "inner"))).IsTrue();
        await Assert.That(document.ScopeDepth).IsEqualTo(2);
        await Assert.That(documents.Sync.Window).IsEqualTo(inner).Because("a new scope shows whole");
        documents.Sync.SetWindow(zoomed);

        await Assert.That(documents.PopScope()).IsTrue();
        await Assert.That(document.ScopeDepth).IsEqualTo(1);
        await Assert.That(documents.Sync.Scope).IsEqualTo(picks.Window);
        await Assert.That(documents.Sync.Window).IsEqualTo(picks.Window).Because("Back shows the view before the scope");

        documents.PushScope(TimeScope.Of(inner, "inner"));
        await Assert.That(documents.ClearScope()).IsTrue();
        await Assert.That(document.IsScoped).IsFalse();
        await Assert.That(documents.Sync.IsScoped).IsFalse();
        await Assert.That(documents.Sync.Bounds).IsEqualTo(document.TimeRange);
        await Assert.That(documents.Sync.Window).IsEqualTo(picks.Window).Because("✕ shows the view before the first scope");
        await Assert.That(whole.IsEmpty).IsFalse();
        await Assert.That(documents.PopScope()).IsFalse();
    }

    [Test]
    public async Task Each_document_keeps_its_scope_across_tab_switches()
    {
        await using var documents = new DocumentsViewModel(new TestUi(), null);
        var (first, picks) = await ChatFixture.OpenAsync(documents);
        documents.Sync.SetWindow(picks.Window, byUser: false);
        documents.FilterToView();
        FileDocumentViewModel second = await CompareFixture.OpenIndexedAsync(documents, "chat-client.nntl");
        await Assert.That(documents.Active).IsEqualTo(second);
        await Assert.That(documents.Sync.IsScoped).IsFalse();
        await Assert.That(documents.Sync.Bounds).IsEqualTo(second.TimeRange);

        documents.Activate(first);
        await Assert.That(documents.Sync.Scope).IsEqualTo(picks.Window);
        await Assert.That(documents.Sync.Window.Start >= picks.Window.Start && documents.Sync.Window.End <= picks.Window.End).IsTrue();
    }

    [Test]
    public async Task Events_and_Ladder_list_only_the_scopes_frames_and_keep_their_capture_numbers()
    {
        await using var documents = new DocumentsViewModel(new TestUi(), null);
        var (document, picks) = await ChatFixture.OpenAsync(documents);
        using var events = new EventsViewModel(documents);
        using var ladder = new LadderViewModel(documents);
        CaptureSession session = document.Session!;
        int all = events.Rows.Count;
        await Assert.That(ladder.Model.Count).IsEqualTo(all);

        documents.Sync.SetWindow(picks.Window, byUser: false);
        documents.FilterToView();
        (int first, int count) = Expected(session, picks.Window);
        await Assert.That(count).IsGreaterThan(0);
        await Assert.That(count).IsLessThan(all);
        await Assert.That(events.Rows.Count).IsEqualTo(count);
        await Assert.That(ladder.Model.Count).IsEqualTo(count);
        await Assert.That(ladder.Model.TotalCount).IsEqualTo(all);
        await Assert.That(events.Rows.TryGetRow(0, out var row)).IsTrue();
        await Assert.That(row.Number).IsEqualTo(first + 1L).Because("a row keeps its frame's number in the capture");
        await Assert.That(picks.Window.Contains(row.Ticks)).IsTrue();
        await Assert.That(ladder.Model[0].Frame).IsEqualTo(row.Frame);

        // A frame outside the scope has no row; the selected chunk (inside) has one.
        FrameKey outside;
        lock (session.Gate) outside = session.FrameOrder[0];
        await Assert.That(events.Rows.IndexOf(outside)).IsEqualTo(-1);
        await Assert.That(ladder.Model.IndexOf(outside)).IsEqualTo(-1);
        await Assert.That(events.Rows.IndexOf(picks.Chunk)).IsGreaterThanOrEqualTo(0);

        // The direction filter applies inside the scope.
        events.Filter.Value = Controls.DataTable.Events.EventsFilter.Inbound;
        int inbound = 0;
        lock (session.Gate)
        {
            foreach (FrameKey key in session.FrameOrder)
                if (key.Direction == TapDirection.Inbound && session.TryGetFrame(key, out FrameRecord f) && picks.Window.Contains(f.StartTicks)) inbound++;
        }

        await Assert.That(events.Rows.Count).IsEqualTo(inbound);

        documents.ClearScope();
        events.Filter.Value = Controls.DataTable.Events.EventsFilter.All;
        await Assert.That(events.Rows.Count).IsEqualTo(all);
        await Assert.That(ladder.Model.Count).IsEqualTo(all);
    }

    [Test]
    public async Task Hex_lays_out_only_the_ops_in_the_scope()
    {
        await using var documents = new DocumentsViewModel(new TestUi(), null);
        var (document, picks) = await ChatFixture.OpenAsync(documents);
        using var hex = new HexViewModel(documents);
        int allOps = hex.Layout.OpCount;
        documents.Sync.SetWindow(picks.Window, byUser: false);
        documents.FilterToView();

        CaptureSession session = document.Session!;
        int expected = 0;
        lock (session.Gate)
        {
            foreach (TapDirection direction in (ReadOnlySpan<TapDirection>)[TapDirection.Inbound, TapDirection.Outbound])
                foreach (StreamOp op in session.GetStore(direction).Ops)
                    if (op.Length > 0 && picks.Window.Contains(op.Ticks)) expected++;
        }

        await Assert.That(expected).IsGreaterThan(0);
        await Assert.That(hex.Layout.OpCount).IsEqualTo(expected);
        await Assert.That(hex.Layout.OpCount).IsLessThan(allOps);
        await Assert.That(hex.Layout.Scope).IsEqualTo(picks.Window);
        for (int i = 0; i < hex.Layout.OpCount; i++)
            await Assert.That(picks.Window.Contains(hex.Layout.GetOp(i).Ticks)).IsTrue();

        documents.ClearScope();
        await Assert.That(hex.Layout.OpCount).IsEqualTo(allOps);
    }

    [Test]
    public async Task The_message_trace_marks_the_steps_outside_the_scope()
    {
        await using var documents = new DocumentsViewModel(new TestUi(), null);
        var (document, picks) = await ChatFixture.OpenAsync(documents);
        using var inspector = new InspectorViewModel(documents);
        documents.Sync.Select(Selection.OfMessage(picks.Message, picks.Chunk));
        MessageTrace trace = documents.Sync.Trace;
        await Assert.That(trace.IsActive).IsTrue();
        await Assert.That(inspector.TraceRows.Any(r => r.IsOutOfScope)).IsFalse();

        // The first half of the traced span.
        TimeWindow half = new(trace.Start, trace.Start + ((trace.End - trace.Start) / 2));
        await Assert.That(documents.PushScope(TimeScope.Of(half, "half"))).IsTrue();
        TraceStepRow[] rows = [.. inspector.TraceRows];
        await Assert.That(rows.Length).IsEqualTo(trace.Steps.Length);
        foreach (TraceStepRow row in rows)
        {
            await Assert.That(row.IsOutOfScope).IsEqualTo(!half.Contains(row.Step.Ticks));
            await Assert.That(row.RowOpacity).IsEqualTo(row.IsOutOfScope ? 0.4 : row.IsInterleaved ? 0.6 : 1);
        }

        int outside = rows.Count(r => r.IsOutOfScope);
        await Assert.That(outside).IsGreaterThan(0);
        await Assert.That(rows.Count(r => !r.IsOutOfScope)).IsGreaterThan(0);
        await Assert.That(inspector.TraceSummary).EndsWith(" · " + outside + " outside the scope");
        await Assert.That(rows.First(r => r.IsOutOfScope).Detail).EndsWith(" · outside the scope");

        documents.ClearScope();
        await Assert.That(inspector.TraceRows.Any(r => r.IsOutOfScope)).IsFalse();
    }

    [Test]
    public async Task Palette_search_lists_only_the_entities_and_frames_in_the_scope()
    {
        await using var documents = new DocumentsViewModel(new TestUi(), null);
        var (document, picks) = await ChatFixture.OpenAsync(documents);
        CaptureSession session = document.Session!;
        PaletteSearch search = new();
        CommandRegistry commands = new();
        PaletteQuery frames = PaletteQuery.Parse("#", PaletteScope.All);
        PaletteQuery entities = PaletteQuery.Parse("", PaletteScope.Entities);

        List<PaletteRow> unscopedFrames = search.Run(frames, session, [], commands);
        List<PaletteRow> unscopedEntities = search.Run(entities, session, [], commands);
        List<PaletteRow> scopedFrames = search.Run(frames, session, [], commands, picks.Window);
        List<PaletteRow> scopedEntities = search.Run(entities, session, [], commands, picks.Window);

        PaletteRow[] frameItems = [.. scopedFrames.Where(r => r.Target.Kind == PaletteTargetKind.Frame)];
        await Assert.That(frameItems.Length).IsGreaterThan(0);
        foreach (PaletteRow row in frameItems) await Assert.That(picks.Window.Contains(row.Target.Start)).IsTrue();
        await Assert.That(unscopedFrames.Count(r => r.Target.Kind == PaletteTargetKind.Frame && !picks.Window.Contains(r.Target.Start)))
            .IsGreaterThan(0);

        PaletteRow[] entityItems = [.. scopedEntities.Where(r => r.Target.Kind == PaletteTargetKind.Entity)];
        PaletteRow[] allEntities = [.. unscopedEntities.Where(r => r.Target.Kind == PaletteTargetKind.Entity)];
        await Assert.That(entityItems.Length).IsGreaterThan(0);
        await Assert.That(entityItems.Length).IsLessThan(allEntities.Length);
        foreach (PaletteRow row in entityItems)
            await Assert.That(picks.Window.Overlaps(row.Target.Start, row.Target.End)).IsTrue().Because(row.Label);
    }

    [Test]
    public async Task Export_defaults_to_the_scope_and_a_range_asked_for_by_name_is_kept()
    {
        await using var documents = new DocumentsViewModel(new TestUi(), null);
        var (document, picks) = await ChatFixture.OpenAsync(documents);
        var unscoped = new ExportSectionDialogViewModel(documents, document);
        await Assert.That(unscoped.Range).IsEqualTo(ExportRangeKind.Window);
        await Assert.That(unscoped.HasScope).IsFalse();
        unscoped.IsScope = true;
        await Assert.That(unscoped.Range).IsEqualTo(ExportRangeKind.Window);

        documents.Sync.SetWindow(picks.Window, byUser: false);
        documents.FilterToView();
        TimeWindow zoomed = new(picks.Window.Start, picks.Window.Start + (picks.Window.Duration / 3));
        documents.Sync.SetWindow(zoomed);
        var dialog = new ExportSectionDialogViewModel(documents, document);
        await Assert.That(dialog.HasScope).IsTrue();
        await Assert.That(dialog.Range).IsEqualTo(ExportRangeKind.Scope);
        await Assert.That(dialog.IsScope).IsTrue();
        await Assert.That(dialog.TryGetRange(out long from, out long to, out _)).IsTrue();
        await Assert.That((from, to)).IsEqualTo((picks.Window.Start, picks.Window.End));

        // The timeline's "Export this range…" asks for the window by name.
        var window = new ExportSectionDialogViewModel(documents, document, ExportRangeKind.Window);
        await Assert.That(window.Range).IsEqualTo(ExportRangeKind.Window);
        window.TryGetRange(out from, out to, out _);
        await Assert.That((from, to)).IsEqualTo((zoomed.Start, zoomed.End));
    }

    [Test]
    public async Task Compare_applies_a_compared_documents_scope_to_the_aligned_axis()
    {
        await using CompareFixture f = await CompareFixture.OpenAsync();
        CompareSession session = f.Compare.Session!;
        TimeWindow range = session.Range;
        await Assert.That(f.Compare.Rows.Bounds).IsEqualTo(range);

        TimeWindow capture = f.A.TimeRange;
        TimeWindow scope = new(capture.Start + (capture.Duration / 4), capture.Start + (capture.Duration / 2));
        await Assert.That(f.A.PushScope(TimeScope.Of(scope, "a"))).IsTrue();
        TimeWindow expected = new(session.Clock.ToCompare(CompareSide.A, scope.Start), session.Clock.ToCompare(CompareSide.A, scope.End));
        await Assert.That(f.Compare.ScopeOnAxis()).IsEqualTo(expected);
        await Assert.That(f.Compare.Rows.Scope).IsEqualTo(expected);
        await Assert.That(f.Compare.Rows.Bounds).IsEqualTo(expected);
        await Assert.That(f.Compare.Rows.Window).IsEqualTo(expected);

        // The window stays on the scope's part of the axis.
        f.Compare.Rows.SetWindow(range);
        await Assert.That(f.Compare.Rows.Window).IsEqualTo(expected);

        f.A.ClearScopes();
        await Assert.That(f.Compare.Rows.Scope.IsEmpty).IsTrue();
        await Assert.That(f.Compare.Rows.Bounds).IsEqualTo(range);
    }

    // The rows a scope shows at the fixture: from the first frame (in capture order) starting at or after its start to
    // the first starting at or after its end.
    private static (int First, int Count) Expected(CaptureSession session, TimeWindow scope)
    {
        lock (session.Gate)
        {
            ReadOnlySpan<FrameKey> order = session.FrameOrder;
            int first = -1, count = 0;
            for (int i = 0; i < order.Length; i++)
            {
                if (!session.TryGetFrame(order[i], out FrameRecord frame) || !scope.Contains(frame.StartTicks)) continue;
                if (first < 0) first = i;
                count++;
            }

            return (first, count);
        }
    }
}
