using System.Buffers;
using System.Diagnostics;
using NexNet.Inspector.Core.Assembly;
using NexNet.Inspector.Core.Compare;
using NexNet.Inspector.Core.Model;
using NexNet.Inspector.Core.Session;
using NexNet.Inspector.Sample;

namespace NexNet.Inspector.Tests.Compare;

/// <summary>The compare engine on the Chat fixtures: the same run twice, its two mirrored views, and a variant run.</summary>
public class CompareSessionTests
{
    public static IEnumerable<Alignment> Alignments() => [Alignment.Relative, Alignment.WallClock, Alignment.OnEvent];

    [Test]
    [MethodDataSource(nameof(Alignments))]
    public async Task The_same_run_compared_with_itself_has_no_differences(Alignment alignment)
    {
        var a = CompareFixtures.Session(FixtureKind.ChatClient);
        var b = CompareFixtures.Copy(FixtureKind.ChatClient, out var file);
        using (file)
        {
            var compare = new CompareSession(a, b);
            compare.Align(alignment);

            await Assert.That(compare.Deltas.Count).IsEqualTo(0);
            await Assert.That(compare.Deltas.MaxSeverity).IsEqualTo(DeltaSeverity.None);
            await Assert.That(compare.Matches.ToArray().All(m => m.Presence == EntityPresence.Both && !m.HasDifferences)).IsTrue();
            await Assert.That(compare.Clock.OffsetB).IsEqualTo(0);
            for (var i = 0; i < compare.Matches.Length; i++)
            {
                await Assert.That(compare.GetStartDelta(compare.Matches[i])).IsEqualTo(0);
                await Assert.That(compare.GetFieldDiff(i).DifferenceCount).IsEqualTo(0).Because(compare.GetLabel(i));
            }
        }
    }

    [Test]
    [MethodDataSource(nameof(Alignments))]
    public async Task Client_and_server_views_of_one_run_match_entity_for_entity(Alignment alignment)
    {
        var client = CompareFixtures.Session(FixtureKind.ChatClient);
        var server = CompareFixtures.Session(FixtureKind.ChatServer);
        var compare = new CompareSession(client, server);
        compare.Align(alignment);

        // Mirrored: every entity of one view is in the other, keyed by wire flow although the tap directions are opposite.
        await Assert.That(compare.Matches.ToArray().All(m => m.Presence == EntityPresence.Both)).IsTrue();
        var attachment = Single(compare, "SendAttachment", CompareEntityKind.Invocation);
        await Assert.That(attachment.Key.Flow).IsEqualTo(WireFlow.ClientToServer);
        await Assert.That(client.Assembler.Invocations[attachment.A.Index].Direction).IsEqualTo(TapDirection.Outbound);
        await Assert.That(server.Assembler.Invocations[attachment.B.Index].Direction).IsEqualTo(TapDirection.Inbound);
        await Assert.That(attachment.HasDifferences).IsFalse();
        await Assert.That(compare.GetFieldDiff(attachment.Index).DifferenceCount).IsEqualTo(0);

        // What differs is what the server recorder's Drop-mode gap cut: the gaps themselves, and only entities whose
        // frames span one (the burst's LogLine channel, the ping totals). Timing agrees within the tolerance.
        var gaps = server.Gaps.ToArray();
        await Assert.That(gaps.Length).IsGreaterThan(0);
        var deltas = compare.Deltas.Entries.ToArray();
        await Assert.That(deltas.Count(d => d.Kind == DeltaKind.Gap)).IsEqualTo(gaps.Length);
        await Assert.That(deltas.Where(d => d.Kind == DeltaKind.Gap).All(d => d.Side == CompareSide.B)).IsTrue();
        var gapDeltas = deltas.Where(d => d.Kind == DeltaKind.Gap).ToArray();
        foreach (var delta in deltas.Where(d => d.Kind == DeltaKind.Entity))
        {
            var match = compare.Matches[delta.Match];
            var spansGap = gapDeltas.Any(g => delta.Time <= g.EndTime && g.Time <= delta.EndTime);
            await Assert.That(spansGap).IsTrue().Because($"{compare.GetLabel(delta.Match)} {match.Key} differs by {delta.Differences}");
            await Assert.That(delta.Differences.HasFlag(EntityDifferences.Timing)).IsFalse();
        }

        var burst = compare.Matches.ToArray().Single(m => m.Key.Kind == CompareEntityKind.Pipe && m.HasDifferences);
        await Assert.That(compare.GetLabel(burst.Index)).IsEqualTo("LogLine");
        await Assert.That(burst.A.Items).IsGreaterThan(burst.B.Items);

        // Its field diff pairs the items both sides kept and lists the ones the gap lost as removed.
        var items = compare.GetFieldDiff(burst.Index);
        var parts = items.Parts.ToArray();
        await Assert.That(parts.All(p => p.Kind == FieldDiffPartKind.ChannelItem)).IsTrue();
        await Assert.That(parts.Count(p => p is { A: not null, B: not null, Differing: 0 })).IsGreaterThanOrEqualTo(burst.B.Items - 2);
        await Assert.That(parts.Count(p => p.B is null)).IsGreaterThanOrEqualTo(burst.A.Items - burst.B.Items);
    }

    [Test]
    public async Task Variant_run_shows_the_changed_payload_and_the_extra_invocation()
    {
        var compare = new CompareSession(CompareFixtures.Session(FixtureKind.ChatClient), CompareFixtures.Session(FixtureKind.ChatClientVariant));
        var sends = compare.Matches.ToArray()
            .Where(m => m.Key.Kind == CompareEntityKind.Invocation && compare.GetLabel(m.Index) == "SendMessage").ToArray();
        await Assert.That(sends.Length).IsEqualTo(ChatScenario.FullMessages + 1);
        await Assert.That(sends.Select(s => s.Key.Ordinal)).IsEquivalentTo(Enumerable.Range(0, sends.Length));

        // Ordinal 0 changed in place, ordinal 1 exists only in B, the rest pair with their counterparts unchanged.
        await Assert.That(sends[0].Presence).IsEqualTo(EntityPresence.Both);
        await Assert.That(sends[0].Differences.HasFlag(EntityDifferences.Content)).IsTrue();
        await Assert.That(sends[0].Severity).IsEqualTo(DeltaSeverity.Medium);
        await Assert.That(sends[1].Presence).IsEqualTo(EntityPresence.OnlyB);
        await Assert.That(sends[1].Severity).IsEqualTo(DeltaSeverity.High);
        foreach (var same in sends.Skip(2))
        {
            await Assert.That(same.Presence).IsEqualTo(EntityPresence.Both);
            await Assert.That(same.Differences & ~EntityDifferences.Timing).IsEqualTo(EntityDifferences.None);
        }

        // The canceled call stays canceled on both sides.
        await Assert.That(sends.Count(s => s.A.Outcome == EntityOutcome.Canceled && s.B.Outcome == EntityOutcome.Canceled)).IsEqualTo(1);

        // Field diff of the changed message: only Text (and the containers above it) changed.
        var changed = compare.GetFieldDiff(sends[0].Index);
        var arguments = changed.GetRows(0).ToArray();
        await Assert.That(changed.Parts[0].Kind).IsEqualTo(FieldDiffPartKind.Arguments);
        var leaves = arguments.Where(r => r.Kind == FieldDiffKind.Changed && !arguments.Any(c => c.Depth > r.Depth && c.Path.StartsWith(r.Path + ".", StringComparison.Ordinal))).ToArray();
        await Assert.That(leaves.Length).IsEqualTo(1);
        var text = leaves[0];
        await Assert.That(text.Path.EndsWith(".Text", StringComparison.Ordinal)).IsTrue().Because(text.Path);
        await Assert.That(arguments.Where(r => r.Kind == FieldDiffKind.Changed).All(r => text.Path.StartsWith(r.Path, StringComparison.Ordinal))).IsTrue();
        await Assert.That(arguments.Count(r => r.Kind == FieldDiffKind.Same)).IsGreaterThanOrEqualTo(3);
        var valueA = changed.GetValue(text, CompareSide.A)!;
        var valueB = changed.GetValue(text, CompareSide.B)!;
        await Assert.That(valueA.GetText(valueA.Fields[text.FieldA])).IsEqualTo("hello everyone");
        await Assert.That(valueB.GetText(valueB.Fields[text.FieldB])).IsEqualTo(Sample.Fixtures.VariantHelloText);

        // The byte ranges point at the field in each message, for the A ⇄ B hex view.
        await Assert.That(text.RangeA.Length).IsEqualTo(valueA.Fields[text.FieldA].Length);
        await Assert.That(text.RangeB.Length).IsEqualTo(text.RangeA.Length + 1);
        await Assert.That(compare.A.Assembler.TryGetPayload(text.RangeA, out var bytesA)).IsTrue();
        await Assert.That(bytesA.ToArray().AsSpan().SequenceEqual(valueA.GetBytes(valueA.Fields[text.FieldA]))).IsTrue();

        // The extra call: every row added, nothing on A's side.
        var added = compare.GetFieldDiff(sends[1].Index);
        await Assert.That(added.Rows.ToArray().All(r => r is { Kind: FieldDiffKind.Added, FieldA: -1 } && r.RangeA.IsNone && !r.RangeB.IsNone)).IsTrue();
        var extra = added.Parts[0].B!;
        await Assert.That(extra.Fields.ToArray().Any(f => extra.GetText(f) == Sample.Fixtures.VariantExtraMessage)).IsTrue();

        // And they are in the Δ list, the inserted call as High.
        await Assert.That(compare.Deltas.IndexOfMatch(sends[0].Index)).IsGreaterThanOrEqualTo(0);
        var insertion = compare.Deltas.Entries[compare.Deltas.IndexOfMatch(sends[1].Index)];
        await Assert.That(insertion.Severity).IsEqualTo(DeltaSeverity.High);
        await Assert.That(insertion.Differences).IsEqualTo(EntityDifferences.Presence);
        await Assert.That(compare.Deltas.CountAtLeast(DeltaSeverity.High)).IsGreaterThanOrEqualTo(1);
    }

    [Test]
    [MethodDataSource(nameof(Alignments))]
    public async Task Delta_list_is_in_time_order_worst_first_at_equal_times(Alignment alignment)
    {
        var compare = new CompareSession(CompareFixtures.Session(FixtureKind.ChatServer), CompareFixtures.Session(FixtureKind.ChatClientVariant));
        compare.Align(alignment);
        var entries = compare.Deltas.Entries.ToArray();
        await Assert.That(entries.Length).IsGreaterThan(0);
        for (var i = 1; i < entries.Length; i++)
            await Assert.That(DeltaList.Compare(entries[i - 1], entries[i])).IsLessThanOrEqualTo(0);

        foreach (var entry in entries.Where(e => e.Kind == DeltaKind.Entity))
        {
            var match = compare.Matches[entry.Match];
            await Assert.That(entry.Time).IsEqualTo(compare.GetStart(match));
            await Assert.That(entry.Severity).IsEqualTo(match.Severity);
            await Assert.That(entry.Severity).IsEqualTo(CompareSession.SeverityOf(match.Differences));
            await Assert.That(compare.Deltas.Entries[compare.Deltas.IndexOfMatch(entry.Match)]).IsEqualTo(entry);
        }

        // Next-difference navigation.
        var middle = entries[entries.Length / 2].Time;
        var next = compare.Deltas.IndexAtOrAfter(middle);
        await Assert.That(entries[next].Time).IsGreaterThanOrEqualTo(middle);
        await Assert.That(next == 0 || entries[next - 1].Time < middle).IsTrue();
        await Assert.That(compare.Deltas.IndexAtOrAfter(long.MaxValue)).IsEqualTo(entries.Length);

        // Group rows count what their entities say.
        foreach (var group in compare.Groups.ToArray())
        {
            var members = compare.Matches.Slice(group.FirstMatch, group.Count).ToArray();
            await Assert.That(members.All(m => m.Key.Group == group.Key)).IsTrue();
            await Assert.That(group.Differing).IsEqualTo(members.Count(m => m.HasDifferences));
        }
    }

    [Test]
    public async Task Equal_times_order_by_severity_then_entities_before_gaps()
    {
        var list = new DeltaList(
        [
            new DeltaEntry(DeltaKind.Gap, 10, 12, -1, CompareSide.B, 0, EntityDifferences.None, DeltaSeverity.Medium),
            new DeltaEntry(DeltaKind.Entity, 10, 20, 3, CompareSide.A, -1, EntityDifferences.Timing, DeltaSeverity.Info),
            new DeltaEntry(DeltaKind.Entity, 5, 20, 2, CompareSide.A, -1, EntityDifferences.Frames, DeltaSeverity.Low),
            new DeltaEntry(DeltaKind.Entity, 10, 20, 1, CompareSide.A, -1, EntityDifferences.Content, DeltaSeverity.Medium),
            new DeltaEntry(DeltaKind.Entity, 10, 20, 0, CompareSide.A, -1, EntityDifferences.Presence, DeltaSeverity.High),
        ], 4);

        await Assert.That(list.Entries.ToArray().Select(e => (e.Kind, e.Match)))
            .IsEquivalentTo([(DeltaKind.Entity, 2), (DeltaKind.Entity, 0), (DeltaKind.Entity, 1), (DeltaKind.Gap, -1), (DeltaKind.Entity, 3)]);
        await Assert.That(list.Entries[1].Match).IsEqualTo(0);
        await Assert.That(list.Entries[3].Kind).IsEqualTo(DeltaKind.Gap);
        await Assert.That(list.MaxSeverity).IsEqualTo(DeltaSeverity.High);
        await Assert.That(list.CountAtLeast(DeltaSeverity.Medium)).IsEqualTo(3);
        await Assert.That(list.IndexOfMatch(3)).IsEqualTo(4);
        await Assert.That(list.IndexAtOrAfter(6)).IsEqualTo(1);
    }

    [Test]
    public async Task On_event_aligns_the_picked_entity_in_both_documents()
    {
        var compare = new CompareSession(CompareFixtures.Session(FixtureKind.ChatClient), CompareFixtures.Session(FixtureKind.ChatClientVariant));

        // Session ready: the ServerGreeting starts at the anchor on both sides.
        compare.Align(Alignment.OnEvent);
        await Assert.That(compare.Clock.EventResolved).IsTrue();
        var ready = compare.Matches.ToArray().Single(m => m.Key is { Kind: CompareEntityKind.Handshake, Id: (ushort)HandshakeItem.ServerGreeting });
        await Assert.That(compare.GetStart(ready, CompareSide.A)).IsEqualTo(compare.Clock.AnchorTime);
        await Assert.That(compare.GetStart(ready, CompareSide.B)).IsEqualTo(compare.Clock.AnchorTime);

        // A given invocation: SendAttachment starts at the same compare time in both, wherever the runs drifted.
        var attachment = Single(compare, "SendAttachment", CompareEntityKind.Invocation);
        compare.Align(Alignment.OnEvent, AlignmentEvent.OnEntity(attachment.Index));
        await Assert.That(compare.GetStartDelta(compare.Matches[attachment.Index])).IsEqualTo(0);
        await Assert.That(compare.GetStart(compare.Matches[attachment.Index], CompareSide.A)).IsEqualTo(compare.Clock.AnchorTime);
        await Assert.That(Math.Min(compare.Clock.OffsetA, compare.Clock.OffsetB)).IsEqualTo(0);

        // Picked frames: the same, by frame.
        var frameA = new FrameKey(TapDirection.Outbound, 5);
        var frameB = new FrameKey(TapDirection.Outbound, 9);
        compare.Align(Alignment.OnEvent, AlignmentEvent.OnFrames(frameA, frameB));
        compare.A.TryGetFrame(frameA, out var a);
        compare.B.TryGetFrame(frameB, out var b);
        await Assert.That(compare.Clock.ToCompare(CompareSide.A, a.StartTicks)).IsEqualTo(compare.Clock.ToCompare(CompareSide.B, b.StartTicks));

        // An entity B lacks cannot pin B: its start is used and the clock says so.
        var onlyB = compare.Matches.ToArray().First(m => m.Presence == EntityPresence.OnlyB);
        compare.Align(Alignment.OnEvent, AlignmentEvent.OnEntity(onlyB.Index));
        await Assert.That(compare.Clock.EventResolved).IsFalse();

        // Relative again: both start at zero.
        compare.Align(Alignment.Relative);
        await Assert.That(compare.Clock.OffsetA).IsEqualTo(0);
        await Assert.That(compare.Clock.OffsetB).IsEqualTo(0);
    }

    [Test]
    public async Task Wall_clock_places_the_mirrored_views_within_the_latency()
    {
        var client = CompareFixtures.Session(FixtureKind.ChatClient);
        var server = CompareFixtures.Session(FixtureKind.ChatServer);
        var compare = new CompareSession(client, server);
        compare.Align(Alignment.WallClock);

        // The client's ClientGreeting write and the server's read are one event a loopback hop apart.
        var greeting = compare.Matches.ToArray().Single(m => m.Key is { Kind: CompareEntityKind.Handshake, Id: (ushort)HandshakeItem.ClientGreeting });
        await Assert.That(Math.Abs(compare.GetStartDelta(greeting))).IsLessThan(compare.Options.TimingTolerance.Ticks);
        await Assert.That(compare.Range.Duration).IsGreaterThan(0);
        await Assert.That(compare.Duration).IsGreaterThanOrEqualTo(Math.Max(
            compare.Clock.ToCompare(CompareSide.A, client.EndTicks), compare.Clock.ToCompare(CompareSide.B, server.EndTicks)));
    }

    [Test]
    public async Task Exactly_two_documents_are_compared()
    {
        var a = CompareFixtures.Session(FixtureKind.ChatClient);
        var b = CompareFixtures.Session(FixtureKind.ChatServer);
        var c = CompareFixtures.Session(FixtureKind.ChatClientVariant);
        await Assert.That(() => CompareSession.Create([a])).Throws<ArgumentException>();
        await Assert.That(() => CompareSession.Create([a, b, c])).Throws<ArgumentException>();
        await Assert.That(() => CompareSession.Create([])).Throws<ArgumentException>();
        var compare = CompareSession.Create([a, b]);
        await Assert.That(compare.A).IsSameReferenceAs(a);
        await Assert.That(compare.B).IsSameReferenceAs(b);

        // Order decides the sides.
        await Assert.That(CompareSession.Create([c, a]).A).IsSameReferenceAs(c);
    }

    [Test]
    public async Task A_new_entity_changes_the_key_of_nothing_after_it()
    {
        var compare = new CompareSession(CompareFixtures.Session(FixtureKind.ChatClient), CompareFixtures.Session(FixtureKind.ChatClientVariant));
        foreach (var match in compare.Matches.ToArray())
            await Assert.That(compare.FindMatch(match.Key)).IsEqualTo(match.Index);
        await Assert.That(compare.FindMatch(new EntityKey(CompareEntityKind.Invocation, WireFlow.ClientToServer, 999, 0))).IsEqualTo(-1);
        await Assert.That(compare.IsStale).IsFalse();
        compare.Refresh();
        await Assert.That(compare.Deltas.Count).IsGreaterThan(0);
    }

    [Test]
    public async Task Full_fixtures_compare_quickly()
    {
        var client = CompareFixtures.Session(FixtureKind.ChatClient);
        var server = CompareFixtures.Session(FixtureKind.ChatServer);
        var variant = CompareFixtures.Session(FixtureKind.ChatClientVariant);
        _ = new CompareSession(client, server);

        // The eager part: the union of each pair, re-timed under every alignment.
        var watch = Stopwatch.StartNew();
        foreach (var (a, b) in new[] { (client, server), (client, variant), (server, variant) })
        {
            var compare = new CompareSession(a, b);
            foreach (var alignment in Alignments())
                compare.Align(alignment);
        }

        var union = watch.Elapsed;
        await Assert.That(union).IsLessThan(TimeSpan.FromSeconds(2));

        // The lazy part, for every entity (the UI builds one at a time): the 768-item burst channel included.
        watch.Restart();
        var all = new CompareSession(client, server);
        var rows = 0;
        for (var i = 0; i < all.Matches.Length; i++)
            rows += all.GetFieldDiff(i).Rows.Length;
        await Assert.That(rows).IsGreaterThan(768);
        await Assert.That(watch.Elapsed).IsLessThan(TimeSpan.FromSeconds(5));
    }

    private static EntityMatch Single(CompareSession compare, string label, CompareEntityKind kind)
        => compare.Matches.ToArray().Single(m => m.Key.Kind == kind && compare.GetLabel(m.Index) == label);
}
