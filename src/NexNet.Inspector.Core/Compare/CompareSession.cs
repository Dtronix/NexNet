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
/// <para>
/// Threads: a compare can be built (with its first alignment) on any thread before it is shared. Once shared, the thread
/// that owns it (the UI) applies alignments and refreshes; <see cref="PrepareAlignment"/> and
/// <see cref="GetFieldDiff"/> may run on any thread meanwhile, because an alignment is computed into new lists and swapped
/// in whole by <see cref="ApplyAlignment"/>, and the field-diff cache is locked.
/// </para>
/// </remarks>
public sealed class CompareSession
{
    /// <summary>The number of documents a compare takes (OQ11).</summary>
    public const int DocumentCount = 2;

    private const int FieldDiffCacheSize = 8;

    private readonly List<(int Match, EntityFieldDiff Diff)> _fieldDiffs = [];
    private readonly Lock _fieldDiffGate = new();

    // Replaced whole, never changed in place once built: readers on other threads keep the lists they read.
    private List<EntityMatch> _matches = [];
    private List<EntityGroupRow> _groups = [];
    private Dictionary<EntityKey, int> _byKey = [];
    private int _union;
    private NntlGap[] _gapsA = [];
    private NntlGap[] _gapsB = [];
    private long _startA, _endA, _startB, _endB;
    private long _versionA, _versionB;

    /// <summary>Compares two sessions.</summary>
    /// <param name="a">Document A (the reference).</param>
    /// <param name="b">Document B.</param>
    /// <param name="options">Settings; <see cref="CompareOptions.Default"/> when null.</param>
    public CompareSession(CaptureSession a, CaptureSession b, CompareOptions? options = null)
        : this(a, b, options, CancellationToken.None)
    {
    }

    /// <summary>Compares two sessions; the build stops early when <paramref name="cancellationToken"/> is cancelled.</summary>
    /// <param name="a">Document A (the reference).</param>
    /// <param name="b">Document B.</param>
    /// <param name="options">Settings; <see cref="CompareOptions.Default"/> when null.</param>
    /// <param name="cancellationToken">Cancels the build (a superseded compare).</param>
    /// <exception cref="OperationCanceledException">The build was cancelled.</exception>
    public CompareSession(CaptureSession a, CaptureSession b, CompareOptions? options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        A = a;
        B = b;
        Options = options ?? CompareOptions.Default;
        Deltas = new DeltaList([], 0);
        Rebuild(cancellationToken);
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
    public void Refresh() => Rebuild(CancellationToken.None);

    private void Rebuild(CancellationToken cancellationToken)
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

        cancellationToken.ThrowIfCancellationRequested();
        lock (B.Gate)
        {
            _versionB = B.Version;
            b = EntityCollector.Collect(B);
            _gapsB = B.Gaps.ToArray();
            _startB = B.StartTicks;
            _endB = B.EndTicks;
        }

        cancellationToken.ThrowIfCancellationRequested();
        BuildUnion(a, b, cancellationToken);
        _union++;
        lock (_fieldDiffGate)
            _fieldDiffs.Clear();
        ApplyAlignment(PrepareAlignment(Clock.Mode, AlignmentEvent, cancellationToken));
    }

    /// <summary>Aligns the documents; On event aligns on "Session ready".</summary>
    public void Align(Alignment mode) => Align(mode, AlignmentEvent.SessionReady);

    /// <summary>Aligns the documents and re-times the differences.</summary>
    /// <param name="mode">The alignment.</param>
    /// <param name="alignmentEvent">For <see cref="Alignment.OnEvent"/>, what to align on.</param>
    public void Align(Alignment mode, AlignmentEvent alignmentEvent) => ApplyAlignment(PrepareAlignment(mode, alignmentEvent));

    /// <summary>
    /// Computes an alignment without changing the compare: the clock, the re-timed differences and the Δ list, for
    /// <see cref="ApplyAlignment"/>. Safe on any thread while the compare is read elsewhere.
    /// </summary>
    /// <param name="mode">The alignment.</param>
    /// <param name="alignmentEvent">For <see cref="Alignment.OnEvent"/>, what to align on.</param>
    /// <param name="cancellationToken">Cancels a superseded alignment.</param>
    /// <exception cref="OperationCanceledException">Cancelled.</exception>
    public CompareAlignment PrepareAlignment(Alignment mode, AlignmentEvent alignmentEvent, CancellationToken cancellationToken = default)
    {
        // One snapshot of the union: an alignment applied meanwhile swaps the lists, it does not change these.
        var union = _union;
        var source = _matches;
        long? eventA = null, eventB = null;
        if (mode == Alignment.OnEvent)
        {
            eventA = ResolveEvent(source, CompareSide.A, alignmentEvent);
            eventB = ResolveEvent(source, CompareSide.B, alignmentEvent);
        }

        var clock = CompareClock.Create(mode, A.Header, _startA, eventA, B.Header, _startB, eventB);
        var duration = Math.Max(0, Math.Max(clock.ToCompare(CompareSide.A, _endA), clock.ToCompare(CompareSide.B, _endB)));

        var matchList = new List<EntityMatch>(source);
        var matches = CollectionsMarshal.AsSpan(matchList);
        var deltas = new List<DeltaEntry>();
        for (var i = 0; i < matches.Length; i++)
        {
            if ((i & 0xFFF) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            ref var match = ref matches[i];
            var differences = Differ(clock, match.Key.Kind, match.A, match.B);
            match = match with { Differences = differences, Severity = SeverityOf(differences) };
            if (differences != EntityDifferences.None)
                deltas.Add(new DeltaEntry(DeltaKind.Entity, GetStart(clock, match), GetEnd(clock, match), i, CompareSide.A, -1, differences, match.Severity));
        }

        var groupList = new List<EntityGroupRow>(_groups);
        var groups = CollectionsMarshal.AsSpan(groupList);
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
            AddGaps(deltas, clock, CompareSide.A, _gapsA);
            AddGaps(deltas, clock, CompareSide.B, _gapsB);
        }

        return new CompareAlignment(this, union, clock, alignmentEvent, duration, matchList, groupList, new DeltaList(deltas.ToArray(), matches.Length));
    }

    /// <summary>Makes a prepared alignment current: swaps in its clock, differences and Δ list.</summary>
    /// <param name="alignment">From this compare's <see cref="PrepareAlignment"/>, since its last refresh.</param>
    /// <exception cref="ArgumentException">Prepared for another compare, or before a refresh rebuilt the union.</exception>
    public void ApplyAlignment(CompareAlignment alignment)
    {
        ArgumentNullException.ThrowIfNull(alignment);
        if (!ReferenceEquals(alignment.Owner, this) || alignment.Union != _union)
            throw new ArgumentException("The alignment was prepared for another compare, or before a refresh.", nameof(alignment));

        _matches = alignment.Matches;
        _groups = alignment.Groups;
        Clock = alignment.Clock;
        AlignmentEvent = alignment.Event;
        Duration = alignment.Duration;
        Deltas = alignment.Deltas;
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

    private static long GetStart(in CompareClock clock, in EntityMatch match)
    {
        if (!match.A.IsPresent)
            return clock.ToCompare(CompareSide.B, match.B.StartTicks);
        if (!match.B.IsPresent)
            return clock.ToCompare(CompareSide.A, match.A.StartTicks);
        return Math.Min(clock.ToCompare(CompareSide.A, match.A.StartTicks), clock.ToCompare(CompareSide.B, match.B.StartTicks));
    }

    private static long GetEnd(in CompareClock clock, in EntityMatch match)
    {
        if (!match.A.IsPresent)
            return clock.ToCompare(CompareSide.B, match.B.EndTicks);
        if (!match.B.IsPresent)
            return clock.ToCompare(CompareSide.A, match.A.EndTicks);
        return Math.Max(clock.ToCompare(CompareSide.A, match.A.EndTicks), clock.ToCompare(CompareSide.B, match.B.EndTicks));
    }

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
    /// <remarks>Safe on any thread; two threads asking for the same entity at once may both build it.</remarks>
    public EntityFieldDiff GetFieldDiff(int match)
    {
        var matches = _matches;
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)match, (uint)matches.Count, nameof(match));
        int union;
        lock (_fieldDiffGate)
        {
            if (TryGetCachedFieldDiff(match, out var cached))
                return cached;
            union = _union;
        }

        // Built outside the lock: a big entity's decode must not hold up the other threads' cached lookups.
        var diff = BuildFieldDiff(matches[match], match);
        lock (_fieldDiffGate)
        {
            if (TryGetCachedFieldDiff(match, out var cached))
                return cached;
            // A refresh rebuilt the union meanwhile: the diff answers the caller but is not kept for the new union.
            if (union != _union)
                return diff;
            if (_fieldDiffs.Count == FieldDiffCacheSize)
                _fieldDiffs.RemoveAt(0);
            _fieldDiffs.Add((match, diff));
        }

        return diff;
    }

    private bool TryGetCachedFieldDiff(int match, out EntityFieldDiff diff)
    {
        for (var i = 0; i < _fieldDiffs.Count; i++)
        {
            if (_fieldDiffs[i].Match == match)
            {
                diff = _fieldDiffs[i].Diff;
                return true;
            }
        }

        diff = null!;
        return false;
    }

    private void BuildUnion(List<SideEntity> a, List<SideEntity> b, CancellationToken cancellationToken)
    {
        _matches = [];
        _groups = [];
        _byKey = [];

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
            cancellationToken.ThrowIfCancellationRequested();
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

    private EntityDifferences Differ(in CompareClock clock, CompareEntityKind kind, in EntitySide a, in EntitySide b)
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
            var start = Math.Abs(clock.ToCompare(CompareSide.B, b.StartTicks) - clock.ToCompare(CompareSide.A, a.StartTicks));
            var durationA = CompareClock.ToTimeSpanTicks(a.DurationTicks, clock.A.Frequency);
            var durationB = CompareClock.ToTimeSpanTicks(b.DurationTicks, clock.B.Frequency);
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

    private static void AddGaps(List<DeltaEntry> deltas, in CompareClock clock, CompareSide side, NntlGap[] gaps)
    {
        for (var i = 0; i < gaps.Length; i++)
        {
            deltas.Add(new DeltaEntry(DeltaKind.Gap, clock.ToCompare(side, gaps[i].FromTicks), clock.ToCompare(side, gaps[i].ToTicks), -1,
                side, i, EntityDifferences.None, DeltaSeverity.Medium));
        }
    }

    private long? ResolveEvent(List<EntityMatch> matches, CompareSide side, in AlignmentEvent alignmentEvent)
    {
        switch (alignmentEvent.Kind)
        {
            case AlignmentEventKind.Entity:
                if ((uint)alignmentEvent.Match >= (uint)matches.Count)
                    return null;
                var entity = matches[alignmentEvent.Match].Get(side);
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

    private EntityFieldDiff BuildFieldDiff(in EntityMatch row, int match)
    {
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
