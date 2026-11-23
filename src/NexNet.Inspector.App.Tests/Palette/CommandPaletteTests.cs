using NexNet.Inspector.App.Model;
using NexNet.Inspector.App.Model.Palette;
using NexNet.Inspector.App.Services.Commands;
using NexNet.Inspector.App.Tests.Capture;
using NexNet.Inspector.App.Tests.Documents;
using NexNet.Inspector.App.Tests.Inspector;
using NexNet.Inspector.App.ViewModels;
using NexNet.Inspector.Core.Assembly;
using NexNet.Inspector.Core.Model;
using NexNet.Inspector.Core.Schema;
using NexNet.Inspector.Core.Session;

namespace NexNet.Inspector.App.Tests.Palette;

/// <summary>
/// The command palette over the Chat fixture: grouped, ranked results for an id (the screen's query), the prefixes
/// and pills, keyboard movement, and what running an entity, a frame, a time, a session or a command does.
/// </summary>
public sealed class CommandPaletteTests
{
    private sealed record Setup(DocumentsViewModel Documents, FileDocumentViewModel Document, CommandRegistry Commands,
        CommandPaletteViewModel Palette, int Invocation, string Id) : IAsyncDisposable
    {
        public CaptureSession Session => Document.Session!;

        public ValueTask DisposeAsync() => Documents.DisposeAsync();
    }

    private static async Task<Setup> OpenAsync()
    {
        DocumentsViewModel documents = new(new TestUi(), null);
        var (document, _) = await ChatFixture.OpenAsync(documents);
        CommandRegistry commands = new();
        CommandPaletteViewModel palette = new(documents, commands);
        CaptureSession session = document.Session!;
        int invocation;
        lock (session.Gate)
        {
            ReadOnlySpan<InvocationRow> rows = session.Assembler.Invocations;
            invocation = -1;
            for (int i = 0; i < rows.Length && invocation < 0; i++)
            {
                if (session.Decoder.TryGetMethod(rows[i], out SchemaMethod? method) && method.Name == "SendAttachment") invocation = i;
            }
        }

        return new Setup(documents, document, commands, palette, invocation, CommandPaletteScreen.Query(session));
    }

    private static PaletteGroup[] Groups(CommandPaletteViewModel palette) =>
        palette.Rows.Where(r => r.IsHeader).Select(r => r.Group).ToArray();

    private static PaletteRow[] Items(CommandPaletteViewModel palette, PaletteGroup group) =>
        palette.Rows.Where(r => r.IsItem && r.Group == group).ToArray();

    [Test]
    public async Task An_invocation_id_lists_that_invocation_first_then_its_frames_then_the_suggested_commands()
    {
        await using Setup s = await OpenAsync();
        s.Palette.Open(s.Id);

        await Assert.That(s.Palette.IsOpen).IsTrue();
        await Assert.That(Groups(s.Palette)).IsEquivalentTo(new[] { PaletteGroup.Entities, PaletteGroup.Frames, PaletteGroup.Commands },
            TUnit.Assertions.Enums.CollectionOrdering.Matching);
        PaletteRow first = Items(s.Palette, PaletteGroup.Entities)[0];
        await Assert.That(first.Label).IsEqualTo("Invocation #" + s.Id + " · SendAttachment");
        await Assert.That(first.Detail!).StartsWith("7 frames · ");
        await Assert.That(first.Glyph).IsEqualTo(PaletteGlyph.Invocation);
        await Assert.That(first.Target).IsEqualTo(first.Target with { Kind = PaletteTargetKind.Entity, Entity = EntityKind.Invocation, Index = s.Invocation });
        // The first item is the keyboard's, with "Enter" in its shortcut column.
        await Assert.That(s.Palette.Selected).IsSameReferenceAs(first);
        await Assert.That(first.ShortcutText).IsEqualTo("Enter");

        // The invocation's own frames lead the frames: MessageStart, then its chunks.
        PaletteRow[] frames = Items(s.Palette, PaletteGroup.Frames);
        await Assert.That(frames.Length).IsEqualTo(PaletteSearch.GroupLimit);
        await Assert.That(frames[0].Label).Contains("MessageStart · inv " + s.Id);
        await Assert.That(frames[1].Label).Contains("MessageChunk 1/5 · inv " + s.Id);
        await Assert.That(frames[0].Detail!).EndsWith(" ms");

        // No command title holds the id: the package's suggestions, Compare naming the invocation.
        string[] commands = Items(s.Palette, PaletteGroup.Commands).Select(r => r.Label).ToArray();
        await Assert.That(commands).IsEquivalentTo(new[]
        {
            "Trace message under cursor", "Go to time…", "Open invocation #" + s.Id + " in Compare", "Export selection as .nntl",
        }, TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task Running_an_entity_selects_and_traces_it_and_brings_it_into_view()
    {
        await using Setup s = await OpenAsync();
        s.Documents.Sync.SetWindow(new TimeWindow(s.Session.StartTicks, s.Session.StartTicks + 1000));
        s.Palette.Open(s.Id);

        await Assert.That(s.Palette.Run()).IsTrue();

        PaletteTarget target = Items(s.Palette, PaletteGroup.Entities)[0].Target;
        await Assert.That(s.Palette.IsOpen).IsFalse();
        await Assert.That(s.Documents.Sync.Selection).IsEqualTo(Selection.OfEntity(EntityKind.Invocation, s.Invocation));
        await Assert.That(s.Documents.Sync.Trace.Kind).IsEqualTo(TraceKind.Invocation);
        await Assert.That(s.Documents.Sync.Playhead).IsEqualTo(target.Start);
        await Assert.That(s.Documents.Sync.Window.Contains(target.Start)).IsTrue();
    }

    [Test]
    public async Task The_frames_prefix_moves_the_pill_and_running_a_frame_selects_it()
    {
        await using Setup s = await OpenAsync();
        s.Palette.Open("#" + s.Id);

        await Assert.That(s.Palette.Scope.Value).IsEqualTo(PaletteScope.Frames);
        await Assert.That(Groups(s.Palette)).IsEquivalentTo(new[] { PaletteGroup.Frames });
        PaletteRow frame = s.Palette.Selected!;
        await Assert.That(s.Palette.Run()).IsTrue();
        await Assert.That(s.Documents.Sync.Selection).IsEqualTo(Selection.OfFrame(frame.Target.Frame));
        await Assert.That(s.Documents.Sync.Playhead).IsEqualTo(frame.Target.Start);
    }

    [Test]
    public async Task The_time_prefix_jumps_the_playhead_and_the_window()
    {
        await using Setup s = await OpenAsync();
        long frequency = s.Session.Header.StopwatchFrequency;
        s.Documents.Sync.SetWindow(TimeWindow.FromDuration(s.Session.StartTicks, frequency / 1000));
        s.Palette.Open(":12.5");

        await Assert.That(Groups(s.Palette)).IsEquivalentTo(new[] { PaletteGroup.Time });
        await Assert.That(s.Palette.Selected!.Label).IsEqualTo("Go to 12.500 ms");
        await Assert.That(s.Palette.Run()).IsTrue();

        long expected = s.Session.StartTicks + TimeWindow.TicksFromMilliseconds(12.5, frequency);
        await Assert.That(s.Documents.Sync.Playhead).IsEqualTo(expected);
        await Assert.That(s.Documents.Sync.Window.Contains(expected)).IsTrue();
        await Assert.That(s.Documents.Sync.Window.Duration).IsEqualTo(frequency / 1000);

        s.Palette.Open(":soon");
        await Assert.That(s.Palette.Selected!.IsEnabled).IsFalse();
        await Assert.That(s.Palette.Run()).IsFalse();
        await Assert.That(s.Palette.IsOpen).IsTrue();
    }

    [Test]
    public async Task The_commands_prefix_lists_matching_commands_and_running_one_closes_the_palette_first()
    {
        await using Setup s = await OpenAsync();
        bool openWhenRun = true;
        s.Commands.Bind(AppCommand.ThemeDark, () => openWhenRun = s.Palette.IsOpen);
        s.Palette.Open(">theme dark");

        await Assert.That(Groups(s.Palette)).IsEquivalentTo(new[] { PaletteGroup.Commands });
        await Assert.That(s.Palette.Selected!.Label).IsEqualTo("Theme: Dark");
        await Assert.That(s.Palette.Run()).IsTrue();
        await Assert.That(openWhenRun).IsFalse();

        // An unbound command is listed, disabled, and Enter does nothing.
        s.Palette.Open(">export");
        await Assert.That(s.Palette.Selected!.Command).IsEqualTo(AppCommand.ExportSelection);
        await Assert.That(s.Palette.Selected!.IsEnabled).IsFalse();
        await Assert.That(s.Palette.Run()).IsFalse();
    }

    [Test]
    public async Task The_sessions_prefix_lists_the_open_documents_and_activates_one()
    {
        await using Setup s = await OpenAsync();
        var (second, _) = await ChatFixture.OpenAsync(s.Documents);
        await Assert.That(s.Documents.Active).IsSameReferenceAs(second);
        s.Palette.Open("@");

        PaletteRow[] sessions = Items(s.Palette, PaletteGroup.Sessions);
        await Assert.That(sessions.Length).IsEqualTo(2);
        await Assert.That(sessions[0].Label).IsEqualTo(s.Document.Title);
        await Assert.That(sessions[1].Detail!).EndsWith(" · active");
        await Assert.That(s.Palette.Run(sessions[0])).IsTrue();
        await Assert.That(s.Documents.Active).IsSameReferenceAs(s.Document);
    }

    [Test]
    public async Task Moving_skips_headings_and_stops_at_either_end()
    {
        await using Setup s = await OpenAsync();
        s.Palette.Open(s.Id);
        int items = s.Palette.Rows.Count(r => r.IsItem);

        s.Palette.Move(1);
        await Assert.That(s.Palette.Selected!.IsItem).IsTrue();
        s.Palette.Move(PaletteSearch.GroupLimit); // across the FRAMES heading
        await Assert.That(s.Palette.Selected!.IsItem).IsTrue();
        s.Palette.Move(1_000);
        await Assert.That(s.Palette.SelectedIndex).IsEqualTo(s.Palette.Rows.Count - 1);
        s.Palette.Move(-1_000);
        await Assert.That(s.Palette.SelectedIndex).IsEqualTo(1);
        await Assert.That(s.Palette.Rows.Count(r => r.IsSelected)).IsEqualTo(1);
        await Assert.That(items).IsGreaterThan(PaletteSearch.GroupLimit);
    }

    [Test]
    public async Task Tab_switches_the_pill_and_drops_a_typed_prefix()
    {
        await using Setup s = await OpenAsync();
        s.Palette.Open(s.Id);

        s.Palette.CycleScope(backwards: false);
        await Assert.That(s.Palette.Scope.Value).IsEqualTo(PaletteScope.Commands);
        await Assert.That(s.Palette.ActiveScope).IsEqualTo(PaletteScope.Commands);
        s.Palette.CycleScope(backwards: false);
        await Assert.That(s.Palette.Scope.Value).IsEqualTo(PaletteScope.Frames);
        await Assert.That(Groups(s.Palette)).IsEquivalentTo(new[] { PaletteGroup.Frames });

        s.Palette.Query = ">trace";
        await Assert.That(s.Palette.Scope.Value).IsEqualTo(PaletteScope.Commands);
        s.Palette.CycleScope(backwards: true);
        await Assert.That(s.Palette.Scope.Value).IsEqualTo(PaletteScope.All);
        await Assert.That(s.Palette.Query).IsEqualTo("trace");

        // Reopening starts over in All.
        s.Palette.Scope.Value = PaletteScope.Entities;
        s.Palette.Open();
        await Assert.That(s.Palette.Scope.Value).IsEqualTo(PaletteScope.All);
    }

    [Test]
    public async Task An_empty_query_lists_commands_and_the_entities_pill_lists_every_entity()
    {
        await using Setup s = await OpenAsync();
        s.Palette.Open();

        await Assert.That(Groups(s.Palette)).IsEquivalentTo(new[] { PaletteGroup.Commands });
        await Assert.That(Items(s.Palette, PaletteGroup.Commands).Length).IsEqualTo(CommandRegistry.Definitions.Count - 1);

        s.Palette.Scope.Value = PaletteScope.Entities;
        int entities;
        lock (s.Session.Gate) entities = 1 + s.Session.Assembler.Invocations.Length + s.Session.Assembler.Pipes.Length;
        await Assert.That(Items(s.Palette, PaletteGroup.Entities).Length).IsEqualTo(Math.Min(entities, PaletteSearch.ScopeLimit));
        await Assert.That(Items(s.Palette, PaletteGroup.Entities)[0].Label).IsEqualTo("Handshake");
    }

    [Test]
    public async Task Nothing_matching_leaves_no_rows_and_Escape_like_Close_raises_Closed()
    {
        await using Setup s = await OpenAsync();
        int closed = 0;
        s.Palette.Closed += () => closed++;
        s.Palette.Open("qqqqzzzz");

        await Assert.That(s.Palette.IsEmpty).IsTrue();
        await Assert.That(s.Palette.Selected).IsNull();
        await Assert.That(s.Palette.Run()).IsFalse();
        s.Palette.Close();
        s.Palette.Close();
        await Assert.That(closed).IsEqualTo(1);
    }

    [Test]
    public async Task Reveal_keeps_a_span_in_view_and_otherwise_centres_it_at_the_same_zoom()
    {
        TimeWindow window = new(1_000, 2_000);

        await Assert.That(CommandPaletteViewModel.Reveal(window, 1_200, 1_300)).IsEqualTo(window);
        await Assert.That(CommandPaletteViewModel.Reveal(window, 5_000, 5_100)).IsEqualTo(new TimeWindow(4_550, 5_550));
        // Longer than the window: its start, a tenth in.
        await Assert.That(CommandPaletteViewModel.Reveal(window, 5_000, 9_000)).IsEqualTo(new TimeWindow(4_900, 5_900));
        await Assert.That(CommandPaletteViewModel.Reveal(default, 10, 20)).IsEqualTo(new TimeWindow(10, 20));
    }

    [Test]
    public async Task The_search_reports_frame_numbers_as_the_Events_table_does()
    {
        await using Setup s = await OpenAsync();
        s.Palette.Open("#1");
        PaletteRow first = s.Palette.Selected!;
        // Frame #1 (the first in capture order) is a whole-id match; "#1" also starts #10, #100 … which rank after it.
        await Assert.That(first.Label).StartsWith("#1 ");
        FrameKey key;
        lock (s.Session.Gate) key = s.Session.FrameOrder[0];
        await Assert.That(first.Target.Frame).IsEqualTo(key);
    }
}
