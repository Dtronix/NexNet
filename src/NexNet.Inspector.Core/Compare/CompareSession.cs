using System.Buffers;
using System.Runtime.InteropServices;
using NexNet.Inspector.Core.Assembly;
using NexNet.Inspector.Core.Format;
using NexNet.Inspector.Core.Model;
using NexNet.Inspector.Core.Schema;
using NexNet.Inspector.Core.Session;
using NexNet.Messages;

namespace NexNet.Inspector.Core.Compare;

/// <summary>
/// Compares exactly two captured sessions (OQ11): the union of their entities matched across both, a per-entity diff,
/// an alignment that puts both on one timeline, the Δ list of differences, and field diffs on demand.
/// </summary>
/// <remarks>
/// <para>
/// Matching: entities are keyed by kind, wire flow and ID (<see cref="EntityKey"/>); within each group (one method's
/// invocations, one method's pipes…) the two sequences are correlated by content digest like the lines of a text diff
/// (<see cref="SequenceCorrelator"/>), so an invocation added in one run is the only one left unmatched and a changed
/// payload pairs with its counterpart at the same place.
/// </para>
/// <para>
/// Mirrored captures: a client-side and a server-side capture of one connection see every frame in opposite tap
/// directions (the client's Outbound is the server's Inbound). Keys and digests use the wire flow
/// (<see cref="WireFlow"/>), never the tap direction, so the two views match entity for entity and the direction
/// itself is never a difference. What remains between them is real: capture gaps on one side (and what they cut), and
/// the latency between a write on one end and the read on the other (within <see cref="CompareOptions.TimingTolerance"/>
/// under <see cref="Alignment.WallClock"/>).
/// </para>
/// <para>
/// Cost: the union (one pass over each document's frames and payloads) is built eagerly when the compare is created and
/// on <see cref="Refresh"/>; changing the alignment only re-times it; decoded field diffs are built only for the entity
/// asked for (<see cref="GetFieldDiff"/>). Building takes each session's <see cref="CaptureSession.Gate"/> in turn.
/// </para>
/// </remarks>
public sealed class CompareSession
{
    /// <summary>The number of documents a compare takes (OQ11).</summary>
    public const int DocumentCount = 2;

    private const int FieldDiffCacheSize = 8;

    private readonly List<EntityMatch> _matches = [];
    private readonly List<EntityGroupRow> _groups = [];
    private readonly Dictionary<EntityKey, int> _byKey = [];
    private readonly List<(int Match, EntityFieldDiff Diff)> _fieldDiffs = [];
    private NntlGap[] _gapsA = [];
    private NntlGap[] _gapsB = [];
    private long _startA, _endA, _startB, _endB;
    private long _versionA, _versionB;

    /// <summary>Compares two sessions.</summary>
    /// <param name="a">Document A (the reference).</param>
    /// <param name="b">Document B.</param>
    /// <param name="options">Settings; <see cref="CompareOptions.Default"/> when null.</param>
    public CompareSession(CaptureSession a, CaptureSession b, CompareOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        A = a;
        B = b;
        Options = options ?? CompareOptions.Default;
        Deltas = new DeltaList([], 0);
        Refresh();
    }

    /// <summary>Compares the documents ticked for Compare; there must be exactly <see cref="DocumentCount"/>.</summary>
    /// <param name="documents">The included documents, A first.</param>
    /// <param name="options">Settings.</param>
    /// <exception cref="ArgumentException">Not exactly two documents.</exception>
    public static CompareSession Create(IReadOnlyList<CaptureSession> documents, CompareOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(documents);
        if (documents.Count != DocumentCount)
        {
            throw new ArgumentException($"Compare takes exactly {DocumentCount} documents; {documents.Count} were given.",
                nameof(documents));
        }

        return new CompareSession(documents[0], documents[1], options);
    }

    /// <summary>Document A.</summary>
    public CaptureSession A { get; }

    /// <summary>Document B.</summary>
    public CaptureSession B { get; }

    /// <summary>The settings.</summary>
    public CompareOptions Options { get; }

    /// <summary>The current alignment's timeline.</summary>
    public CompareClock Clock { get; private set; }

    /// <summary>The current alignment.</summary>
    public Alignment Alignment => Clock.Mode;

    /// <summary>The event of an <see cref="Alignment.OnEvent"/> alignment.</summary>
    public AlignmentEvent AlignmentEvent { get; private set; } = AlignmentEvent.SessionReady;

    /// <summary>The entity union: groups in sidebar order (kind, then ID, then flow), each group's entities in order.</summary>
    public ReadOnlySpan<EntityMatch> Matches => CollectionsMarshal.AsSpan(_matches);

    /// <summary>The groups of <see cref="Matches"/>.</summary>
    public ReadOnlySpan<EntityGroupRow> Groups => CollectionsMarshal.AsSpan(_groups);

    /// <summary>The differences, in timeline order.</summary>
    public DeltaList Deltas { get; private set; }

    /// <summary>Compare time where the later of the two sessions ends.</summary>
    public long Duration { get; private set; }

    /// <summary>The whole compare timeline, at <see cref="CompareClock.Frequency"/>.</summary>
    public TimeWindow Range => TimeWindow.FromDuration(0, Duration);

    /// <summary>True when either session changed (a live capture) since the union was built; see <see cref="Refresh"/>.</summary>
    public bool IsStale => A.Version != _versionA || B.Version != _versionB;

    /// <summary>One document.</summary>
    public CaptureSession Get(CompareSide side) => side == CompareSide.A ? A : B;

    /// <summary>Rebuilds the entity union from both sessions as they are now and re-applies the alignment.</summary>
    public void Refresh()
    {
        List<SideEntity> a, b;
        lock (A.Gate)
        {
            _versionA = A.Version;
            a = EntityCollector.Collect(A);
            _gapsA = A.Gaps.ToArray();
            _startA = A.StartTicks;
            _endA = A.EndTicks;
        }

        lock (B.Gate)
        {
            _versionB = B.Version;
            b = EntityCollector.Collect(B);
            _gapsB = B.Gaps.ToArray();
            _startB = B.StartTicks;
            _endB = B.EndTicks;
        }

        BuildUnion(a, b);
        _fieldDiffs.Clear();
        Align(Clock.Mode, AlignmentEvent);
    }

    /// <summary>Aligns the documents; On event aligns on "Session ready".</summary>
    public void Align(Alignment mode) => Align(mode, AlignmentEvent.SessionReady);

    /// <summary>Aligns the documents and re-times the differences.</summary>
    /// <param name="mode">The alignment.</param>
    /// <param name="alignmentEvent">For <see cref="Alignment.OnEvent"/>, what to align on.</param>
    public void Align(Alignment mode, AlignmentEvent alignmentEvent)
    {
        AlignmentEvent = alignmentEvent;
        long? eventA = null, eventB = null;
        if (mode == Alignment.OnEvent)
        {
            eventA = ResolveEvent(CompareSide.A, alignmentEvent);
            eventB = ResolveEvent(CompareSide.B, alignmentEvent);
        }

        Clock = CompareClock.Create(mode, A.Header, _startA, eventA, B.Header, _startB, eventB);
        Duration = Math.Max(0, Math.Max(Clock.ToCompare(CompareSide.A, _endA), Clock.ToCompare(CompareSide.B, _endB)));

        var matches = CollectionsMarshal.AsSpan(_matches);
        var deltas = new List<DeltaEntry>();
        for (var i = 0; i < matches.Length; i++)
        {
            ref var match = ref matches[i];
            var differences = Differ(match.Key.Kind, match.A, match.B);
            match = match with { Differences = differences, Severity = SeverityOf(differences) };
            if (differences != EntityDifferences.None)
                deltas.Add(new DeltaEntry(DeltaKind.Entity, GetStart(match), GetEnd(match), i, CompareSide.A, -1, differences, match.Severity));
        }

        var groups = CollectionsMarshal.AsSpan(_groups);
        for (var g = 0; g < groups.Length; g++)
        {
            ref var group = ref groups[g];
            var differing = 0;
            var severity = DeltaSeverity.None;
            foreach (ref readonly var match in matches.Slice(group.FirstMatch, group.Count))
            {
                if (!match.HasDifferences)
                    continue;
                differing++;
                if (match.Severity > severity)
                    severity = match.Severity;
            }

            group = group with { Differing = differing, Severity = severity };
        }

        if (Options.IncludeGaps)
        {
            AddGaps(deltas, CompareSide.A, _gapsA);
            AddGaps(deltas, CompareSide.B, _gapsB);
        }

        Deltas = new DeltaList(deltas.ToArray(), matches.Length);
    }

    /// <summary>The union index of an entity; -1 when no document holds it.</summary>
    public int FindMatch(in EntityKey key) => _byKey.TryGetValue(key, out var index) ? index : -1;

    /// <summary>Compare time of an entity's first frame: the earlier of the two sides.</summary>
    public long GetStart(in EntityMatch match)
    {
        if (!match.A.IsPresent)
            return GetStart(match, CompareSide.B);
        if (!match.B.IsPresent)
            return GetStart(match, CompareSide.A);
        return Math.Min(GetStart(match, CompareSide.A), GetStart(match, CompareSide.B));
    }

    /// <summary>Compare time of an entity's last frame: the later of the two sides.</summary>
    public long GetEnd(in EntityMatch match)
    {
        if (!match.A.IsPresent)
            return GetEnd(match, CompareSide.B);
        if (!match.B.IsPresent)
            return GetEnd(match, CompareSide.A);
        return Math.Max(GetEnd(match, CompareSide.A), GetEnd(match, CompareSide.B));
    }

    /// <summary>Compare time of an entity's first frame in one document.</summary>
    public long GetStart(in EntityMatch match, CompareSide side) => Clock.ToCompare(side, match.Get(side).StartTicks);

    /// <summary>Compare time of an entity's last frame in one document.</summary>
    public long GetEnd(in EntityMatch match, CompareSide side) => Clock.ToCompare(side, match.Get(side).EndTicks);

    /// <summary>How much later the entity starts in B than in A on the compare timeline (Δt); 0 unless both hold it.</summary>
    public long GetStartDelta(in EntityMatch match)
        => match.Presence == EntityPresence.Both ? GetStart(match, CompareSide.B) - GetStart(match, CompareSide.A) : 0;

    /// <summary>How much longer the entity lasts in B than in A, in compare ticks; 0 unless both hold it.</summary>
    public long GetDurationDelta(in EntityMatch match)
        => match.Presence == EntityPresence.Both
            ? CompareClock.ToTimeSpanTicks(match.B.DurationTicks, Clock.B.Frequency) - CompareClock.ToTimeSpanTicks(match.A.DurationTicks, Clock.A.Frequency)
            : 0;

    /// <summary>A display name for an entity: its method, channel or collection name, or its message type.</summary>
    public string GetLabel(int match)
    {
        var row = _matches[match];
        var side = row.A.IsPresent ? CompareSide.A : CompareSide.B;
        var session = Get(side);
        var entity = row.Get(side);
        switch (row.Key.Kind)
        {
            case CompareEntityKind.Control:
                return ((MessageType)row.Key.Id).ToString();
            case CompareEntityKind.Handshake:
                return (HandshakeItem)row.Key.Id switch
                {
                    HandshakeItem.Preamble => "Preamble",
                    HandshakeItem.ClientGreeting => "ClientGreeting",
                    HandshakeItem.ClientReconnection => "ClientGreetingReconnection",
                    _ => "ServerGreeting",
                };
        }

        lock (session.Gate)
        {
            var decoder = session.Decoder;
            var assembler = session.Assembler;
            switch (row.Key.Kind)
            {
                case CompareEntityKind.Invocation when (uint)entity.Index < (uint)assembler.Invocations.Length:
                    var invocation = assembler.Invocations[entity.Index];
                    if (decoder.TryGetMethod(invocation, out var method))
                        return method.Name;
                    if (invocation.RequestParsed && decoder.TryGetCollection(invocation.MethodId, out var called))
                        return called.Name;
                    break;
                case CompareEntityKind.Pipe:
                    var pipeLabel = decoder.GetPipeLabel(entity.Index);
                    if (pipeLabel != null)
                        return pipeLabel;
                    var pipe = assembler.Pipes[entity.Index];
                    if ((uint)pipe.OpenerInvocation < (uint)assembler.Invocations.Length
                        && decoder.TryGetMethod(assembler.Invocations[pipe.OpenerInvocation], out var opener))
                        return opener.Name;
                    break;
                case CompareEntityKind.Collection:
                    if (decoder.TryGetCollection(row.Key.Id, out var collection))
                        return collection.Name;
                    break;
            }
        }

        return row.Key.Id == EntityKey.UnknownId
            ? row.Key.Kind.ToString()
            : $"{row.Key.Kind} #{row.Key.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
    }

    /// <summary>
    /// The field diff of an entity: its decoded values compared field by field. Built on first request and kept for the
    /// few entities asked for last; <see cref="Refresh"/> drops them.
    /// </summary>
    /// <param name="match">Index in <see cref="Matches"/>.</param>
    public EntityFieldDiff GetFieldDiff(int match)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)match, (uint)_matches.Count, nameof(match));
        for (var i = 0; i < _fieldDiffs.Count; i++)
        {
            if (_fieldDiffs[i].Match == match)
                return _fieldDiffs[i].Diff;
        }

        var diff = BuildFieldDiff(match);
        if (_fieldDiffs.Count == FieldDiffCacheSize)
            _fieldDiffs.RemoveAt(0);
        _fieldDiffs.Add((match, diff));
        return diff;
    }

    private void BuildUnion(List<SideEntity> a, List<SideEntity> b)
    {
        _matches.Clear();
        _groups.Clear();
        _byKey.Clear();

        var groupsA = GroupOf(a);
        var groupsB = GroupOf(b);
        var keys = new List<EntityGroupKey>(groupsA.Keys);
        foreach (var key in groupsB.Keys)
        {
            if (!groupsA.ContainsKey(key))
                keys.Add(key);
        }

        keys.Sort(static (x, y) =>
        {
            var c = x.Kind.CompareTo(y.Kind);
            if (c != 0)
                return c;
            c = x.Id.CompareTo(y.Id);
            return c != 0 ? c : x.Flow.CompareTo(y.Flow);
        });

        var pairs = new List<(int A, int B)>();
        foreach (var key in keys)
        {
            var inA = groupsA.TryGetValue(key, out var listA) ? listA : [];
            var inB = groupsB.TryGetValue(key, out var listB) ? listB : [];
            var digestsA = Digests(a, inA);
            var digestsB = Digests(b, inB);
            pairs.Clear();
            SequenceCorrelator.Correlate(digestsA, digestsB, Options.MaxCorrelationCells, pairs);

            var first = _matches.Count;
            for (var ordinal = 0; ordinal < pairs.Count; ordinal++)
            {
                var (ia, ib) = pairs[ordinal];
                var entityKey = new EntityKey(key.Kind, key.Flow, key.Id, ordinal);
                _byKey[entityKey] = _matches.Count;
                _matches.Add(new EntityMatch(_matches.Count, entityKey, ia >= 0 ? a[inA[ia]].Side : EntitySide.Absent,
                    ib >= 0 ? b[inB[ib]].Side : EntitySide.Absent, EntityDifferences.None, DeltaSeverity.None));
            }

            _groups.Add(new EntityGroupRow(key, first, pairs.Count, 0, DeltaSeverity.None));
        }
    }

    private static Dictionary<EntityGroupKey, List<int>> GroupOf(List<SideEntity> entities)
    {
        var groups = new Dictionary<EntityGroupKey, List<int>>();
        for (var i = 0; i < entities.Count; i++)
        {
            if (!groups.TryGetValue(entities[i].Group, out var list))
                groups[entities[i].Group] = list = [];
            list.Add(i);
        }

        return groups;
    }

    private static ulong[] Digests(List<SideEntity> entities, List<int> group)
    {
        var digests = new ulong[group.Count];
        for (var i = 0; i < digests.Length; i++)
            digests[i] = entities[group[i]].Side.ContentHash;
        return digests;
    }

    private EntityDifferences Differ(CompareEntityKind kind, in EntitySide a, in EntitySide b)
    {
        if (!a.IsPresent || !b.IsPresent)
            return EntityDifferences.Presence;

        var differences = EntityDifferences.None;
        if (a.Outcome != b.Outcome)
            differences |= EntityDifferences.Outcome;
        if (a.ContentHash != b.ContentHash)
            differences |= EntityDifferences.Content;
        if (a.Items != b.Items)
            differences |= EntityDifferences.Items;
        if (a.Bytes != b.Bytes)
            differences |= EntityDifferences.Bytes;
        if (a.Frames != b.Frames)
            differences |= EntityDifferences.Frames;

        // A control group's first and last frames say nothing about any one frame.
        if (kind != CompareEntityKind.Control && a.Frames > 0 && b.Frames > 0)
        {
            var tolerance = Options.TimingTolerance.Ticks;
            var start = Math.Abs(Clock.ToCompare(CompareSide.B, b.StartTicks) - Clock.ToCompare(CompareSide.A, a.StartTicks));
            var durationA = CompareClock.ToTimeSpanTicks(a.DurationTicks, Clock.A.Frequency);
            var durationB = CompareClock.ToTimeSpanTicks(b.DurationTicks, Clock.B.Frequency);
            var duration = Math.Abs(durationB - durationA);
            if (start > tolerance || (duration > tolerance && duration > Options.DurationTolerance * Math.Max(durationA, durationB)))
                differences |= EntityDifferences.Timing;
        }

        return differences;
    }

    /// <summary>The severity of a set of differences: the worst one.</summary>
    public static DeltaSeverity SeverityOf(EntityDifferences differences)
    {
        if ((differences & (EntityDifferences.Presence | EntityDifferences.Outcome)) != 0)
            return DeltaSeverity.High;
        if ((differences & (EntityDifferences.Content | EntityDifferences.Items)) != 0)
            return DeltaSeverity.Medium;
        if ((differences & (EntityDifferences.Bytes | EntityDifferences.Frames)) != 0)
            return DeltaSeverity.Low;
        return differences == EntityDifferences.None ? DeltaSeverity.None : DeltaSeverity.Info;
    }

    private void AddGaps(List<DeltaEntry> deltas, CompareSide side, NntlGap[] gaps)
    {
        for (var i = 0; i < gaps.Length; i++)
        {
            deltas.Add(new DeltaEntry(DeltaKind.Gap, Clock.ToCompare(side, gaps[i].FromTicks), Clock.ToCompare(side, gaps[i].ToTicks), -1,
                side, i, EntityDifferences.None, DeltaSeverity.Medium));
        }
    }

    private long? ResolveEvent(CompareSide side, in AlignmentEvent alignmentEvent)
    {
        switch (alignmentEvent.Kind)
        {
            case AlignmentEventKind.Entity:
                if ((uint)alignmentEvent.Match >= (uint)_matches.Count)
                    return null;
                var entity = _matches[alignmentEvent.Match].Get(side);
                return entity.IsPresent && entity.Frames > 0 ? entity.StartTicks : null;
            case AlignmentEventKind.Frames:
                var session = Get(side);
                lock (session.Gate)
                {
                    return session.TryGetFrame(side == CompareSide.A ? alignmentEvent.FrameA : alignmentEvent.FrameB, out var frame)
                        ? frame.StartTicks
                        : null;
                }

            default:
                var document = Get(side);
                lock (document.Gate)
                {
                    foreach (ref readonly var greeting in document.Assembler.Greetings)
                    {
                        if (greeting.Kind == GreetingKind.Server && document.Assembler.TryGetFrame(greeting.Direction, greeting.Seq, out var ready))
                            return ready.StartTicks;
                    }
                }

                return null;
        }
    }

    private EntityFieldDiff BuildFieldDiff(int match)
    {
        var row = _matches[match];
        var builder = new FieldDiffBuilder();
        switch (row.Key.Kind)
        {
            case CompareEntityKind.Invocation:
                var (argumentsA, resultA) = DecodeInvocation(CompareSide.A, row.A);
                var (argumentsB, resultB) = DecodeInvocation(CompareSide.B, row.B);
                if (argumentsA != null || argumentsB != null)
                    builder.AddPart(FieldDiffPartKind.Arguments, row.Key.Flow, 0, argumentsA, argumentsB);
                if (resultA != null || resultB != null)
                {
                    var back = row.Key.Flow == WireFlow.ClientToServer ? WireFlow.ServerToClient : WireFlow.ClientToServer;
                    builder.AddPart(FieldDiffPartKind.Result, back, 0, resultA, resultB);
                }

                break;
            case CompareEntityKind.Pipe:
            case CompareEntityKind.Collection:
                var dataA = DecodePipe(CompareSide.A, row.Key.Kind, row.A, out var kindA);
                var dataB = DecodePipe(CompareSide.B, row.Key.Kind, row.B, out var kindB);
                var partKind = row.A.IsPresent ? kindA : kindB;
                var pairs = new List<(int A, int B)>();
                for (var f = 0; f < 2; f++)
                {
                    var valuesA = dataA[f];
                    var valuesB = dataB[f];
                    pairs.Clear();
                    SequenceCorrelator.Correlate(Digests(valuesA), Digests(valuesB), Options.MaxCorrelationCells, pairs);
                    for (var ordinal = 0; ordinal < pairs.Count; ordinal++)
                    {
                        var (ia, ib) = pairs[ordinal];
                        builder.AddPart(partKind, (WireFlow)f, ordinal, ia >= 0 ? valuesA[ia].Value : null, ib >= 0 ? valuesB[ib].Value : null);
                    }
                }

                break;
            case CompareEntityKind.Handshake when row.Key.Id != (ushort)HandshakeItem.Preamble:
                var greetingA = DecodeGreeting(CompareSide.A, row.A);
                var greetingB = DecodeGreeting(CompareSide.B, row.B);
                if (greetingA != null || greetingB != null)
                    builder.AddPart(FieldDiffPartKind.Greeting, row.Key.Flow, 0, greetingA, greetingB);
                break;
        }

        return builder.Build(match);
    }

    private (DecodedValue? Arguments, DecodedValue? Result) DecodeInvocation(CompareSide side, in EntitySide entity)
    {
        if (!entity.IsPresent)
            return (null, null);
        var session = Get(side);
        lock (session.Gate)
        {
            var row = session.Assembler.Invocations[entity.Index];
            return (row.Arguments.IsNone ? null : session.Decoder.DecodeArguments(row), row.Value.IsNone ? null : session.Decoder.DecodeResult(row));
        }
    }

    private DecodedValue? DecodeGreeting(CompareSide side, in EntitySide entity)
    {
        if (!entity.IsPresent)
            return null;
        var session = Get(side);
        lock (session.Gate)
        {
            var assembler = session.Assembler;
            var greeting = assembler.Greetings[entity.Index];
            if (!assembler.TryGetFrame(greeting.Direction, greeting.Seq, out var frame))
                return null;
            var range = new PayloadRange(MessageRef.ForFrame(greeting.Direction, greeting.Seq), 0, frame.BodyLength);
            if (!assembler.TryGetPayload(range, out var bytes))
                return null;
            return session.Decoder.Decoder.DecodeMessagePack(bytes.ToArray(), range, greeting.Kind == GreetingKind.Server ? "ServerGreeting" : "ClientGreeting");
        }
    }

    // The values a pipe carried, per flow: channel items or collection messages decoded, raw runs as bytes.
    private List<(DecodedValue Value, ulong Digest)>[] DecodePipe(CompareSide side, CompareEntityKind kind, in EntitySide entity,
        out FieldDiffPartKind partKind)
    {
        List<(DecodedValue Value, ulong Digest)>[] values = [[], []];
        partKind = kind == CompareEntityKind.Collection ? FieldDiffPartKind.CollectionMessage : FieldDiffPartKind.PipeData;
        if (!entity.IsPresent)
            return values;

        var session = Get(side);
        lock (session.Gate)
        {
            var assembler = session.Assembler;
            var role = session.Role;
            var pipe = kind == CompareEntityKind.Collection ? assembler.Collections[entity.Index].Pipe : entity.Index;
            var row = assembler.Pipes[pipe];
            // Items split before a gap stay valid when the pipe stops being split (ChannelUnavailable), so they are
            // still what is compared; only a pipe that never yielded an item falls back to its raw runs.
            if (row.Content != PipeContent.Raw)
            {
                foreach (ref readonly var item in assembler.ChannelItems)
                {
                    if (item.Pipe != pipe)
                        continue;
                    var hasher = new ContentHasher();
                    EntityCollector.AppendPayload(assembler, item.Value, ref hasher);
                    values[(int)EntityCollector.FlowOf(role, item.Direction)].Add((session.Decoder.DecodeChannelItem(item), hasher.Finish()));
                }

                if (values[0].Count + values[1].Count > 0 || !row.ChannelUnavailable)
                {
                    if (row.Content == PipeContent.Channel)
                        partKind = FieldDiffPartKind.ChannelItem;
                    return values;
                }
            }

            partKind = FieldDiffPartKind.PipeData;
            for (var f = 0; f < 2; f++)
            {
                foreach (var run in assembler.GetPipeRuns(pipe, EntityCollector.DirectionOf(role, (WireFlow)f)))
                {
                    var message = assembler.Messages[run];
                    var range = new PayloadRange(message.Ref, 0, message.Length);
                    if (!assembler.TryGetPayload(range, out var bytes))
                        continue;
                    var hasher = new ContentHasher();
                    hasher.Append(bytes);
                    values[f].Add((SchemaDecoder.DecodeRaw(bytes.ToArray(), range, DecodeProblem.None), hasher.Finish()));
                }
            }
        }

        return values;
    }

    private static ulong[] Digests(List<(DecodedValue Value, ulong Digest)> values)
    {
        var digests = new ulong[values.Count];
        for (var i = 0; i < digests.Length; i++)
            digests[i] = values[i].Digest;
        return digests;
    }
}
