using NexNet.Inspector.Core.Assembly;
using NexNet.Inspector.Core.Schema;

namespace NexNet.Inspector.Core.Compare;

/// <summary>
/// Names one entity of the union so that the same entity has the same key in both documents, whichever end recorded
/// them: the kind, the wire flow (not the tap direction), an ID and an ordinal.
/// </summary>
/// <param name="Kind">The kind.</param>
/// <param name="Flow">
/// Invocation: caller to callee. Pipe and collection: the flow of the invocation that opened it. Control and
/// handshake: the flow of the frames.
/// </param>
/// <param name="Id">
/// Invocation: the method (or collection) ID. Pipe: the opening method's ID. Collection: the collection ID. Control:
/// the message type. Handshake: a <see cref="HandshakeItem"/>. <see cref="UnknownId"/> when the opening request was not
/// captured.
/// </param>
/// <param name="Ordinal">
/// Position among the union's entities with the same kind, flow and ID, after correlation (see
/// <see cref="CompareSession"/>): an entity inserted in one run does not shift the keys of the ones after it.
/// </param>
public readonly record struct EntityKey(CompareEntityKind Kind, WireFlow Flow, ushort Id, int Ordinal)
{
    /// <summary>The ID of an entity whose opening request (and so its method) was not captured.</summary>
    public const ushort UnknownId = ushort.MaxValue;

    /// <summary>The key of the group the entity belongs to: the same kind, flow and ID.</summary>
    public EntityGroupKey Group => new(Kind, Flow, Id);
}

/// <summary>The entities of one kind, flow and ID: one method's invocations, one method's pipes, one control type.</summary>
/// <param name="Kind">The kind.</param>
/// <param name="Flow">The flow.</param>
/// <param name="Id">The ID; see <see cref="EntityKey.Id"/>.</param>
public readonly record struct EntityGroupKey(CompareEntityKind Kind, WireFlow Flow, ushort Id);

/// <summary>
/// One entity as one document holds it: where it is in that document's rows and what the wire shows of it. Ticks are
/// the document's own Stopwatch ticks; <see cref="CompareClock"/> places them on the compare timeline.
/// </summary>
public readonly record struct EntitySide
{
    /// <summary>The side of an entity the document does not hold.</summary>
    public static EntitySide Absent => new() { Index = -1 };

    /// <summary>True when the document holds the entity.</summary>
    public bool IsPresent { get; init; }

    /// <summary>
    /// Index into the document's <see cref="MessageAssembler.Invocations"/>, <see cref="MessageAssembler.Pipes"/>,
    /// <see cref="MessageAssembler.Collections"/> or <see cref="MessageAssembler.Greetings"/>; for a preamble, its frame's
    /// sequence number; -1 for control and absent entities.
    /// </summary>
    public int Index { get; init; }

    /// <summary>Stopwatch ticks of the entity's first frame; 0 when it has none.</summary>
    public long StartTicks { get; init; }

    /// <summary>Stopwatch ticks of the entity's last frame; 0 when it has none.</summary>
    public long EndTicks { get; init; }

    /// <summary>Wire bytes of the entity's frames (headers included).</summary>
    public long Bytes { get; init; }

    /// <summary>Frames of the entity.</summary>
    public int Frames { get; init; }

    /// <summary>Channel items or collection messages; 0 for other kinds.</summary>
    public int Items { get; init; }

    /// <summary>How the entity ended.</summary>
    public EntityOutcome Outcome { get; init; }

    /// <summary>
    /// A 64-bit digest of the entity's payload (arguments and result, pipe data per flow, greeting body): equal digests
    /// mean equal content. 0 for kinds without content.
    /// </summary>
    public ulong ContentHash { get; init; }

    /// <summary>Ticks between the first and the last frame.</summary>
    public long DurationTicks => EndTicks - StartTicks;
}

/// <summary>One row of the entity union: an entity and how each document holds it.</summary>
/// <param name="Index">Position in <see cref="CompareSession.Matches"/>.</param>
/// <param name="Key">The entity.</param>
/// <param name="A">Document A's side.</param>
/// <param name="B">Document B's side.</param>
/// <param name="Differences">What differs (timing as the current alignment places the sides).</param>
/// <param name="Severity">The worst of <paramref name="Differences"/>.</param>
public readonly record struct EntityMatch(int Index, EntityKey Key, EntitySide A, EntitySide B, EntityDifferences Differences,
    DeltaSeverity Severity)
{
    /// <summary>Which documents hold the entity.</summary>
    public EntityPresence Presence => A.IsPresent ? B.IsPresent ? EntityPresence.Both : EntityPresence.OnlyA : EntityPresence.OnlyB;

    /// <summary>True when anything differs.</summary>
    public bool HasDifferences => Differences != EntityDifferences.None;

    /// <summary>One side.</summary>
    /// <param name="side">The side.</param>
    public EntitySide Get(CompareSide side) => side == CompareSide.A ? A : B;
}

/// <summary>A group of the union, for the Entities sidebar ("SendMessage ×1,204" with its Δ marker).</summary>
/// <param name="Key">The group.</param>
/// <param name="FirstMatch">Index of its first entity in <see cref="CompareSession.Matches"/>; its entities are contiguous.</param>
/// <param name="Count">Entities in the group.</param>
/// <param name="Differing">Entities with differences.</param>
/// <param name="Severity">The worst severity among them.</param>
public readonly record struct EntityGroupRow(EntityGroupKey Key, int FirstMatch, int Count, int Differing, DeltaSeverity Severity);

/// <summary>One difference of the Δ list: an entity whose sides differ, or a gap in one capture.</summary>
/// <param name="Kind">Entity or gap.</param>
/// <param name="Time">Compare time where it starts (see <see cref="CompareClock"/>).</param>
/// <param name="EndTime">Compare time where it ends.</param>
/// <param name="Match">The entity's index in <see cref="CompareSession.Matches"/>; -1 for a gap.</param>
/// <param name="Side">For a gap, the document it is in; A otherwise.</param>
/// <param name="Gap">For a gap, its index in that document's <see cref="Session.CaptureSession.Gaps"/>; -1 otherwise.</param>
/// <param name="Differences">What differs; None for a gap.</param>
/// <param name="Severity">How much it matters.</param>
public readonly record struct DeltaEntry(DeltaKind Kind, long Time, long EndTime, int Match, CompareSide Side, int Gap,
    EntityDifferences Differences, DeltaSeverity Severity);

/// <summary>A decoded value of an entity compared field by field: arguments, a result, one channel item…</summary>
/// <param name="Kind">What the value is.</param>
/// <param name="Flow">The flow it travelled.</param>
/// <param name="Ordinal">For items, messages and runs: the position in the correlated sequence of that flow; 0 otherwise.</param>
/// <param name="A">Document A's value; null when A has none.</param>
/// <param name="B">Document B's value; null when B has none.</param>
/// <param name="FirstRow">Index of the part's first row in <see cref="EntityFieldDiff.Rows"/>.</param>
/// <param name="RowCount">Rows of the part.</param>
/// <param name="Differing">Rows that are not <see cref="FieldDiffKind.Same"/>.</param>
public readonly record struct FieldDiffPart(FieldDiffPartKind Kind, WireFlow Flow, int Ordinal, DecodedValue? A, DecodedValue? B,
    int FirstRow, int RowCount, int Differing);

/// <summary>
/// One row of the Field diff table: a field of A, of B or of both, in pre-order (a parent before its children). The
/// byte ranges are in message coordinates, for the A ⇄ B hex view (<see cref="MessageAssembler.GetFragments"/>).
/// </summary>
/// <param name="Part">Index into <see cref="EntityFieldDiff.Parts"/>.</param>
/// <param name="Path">Field names from the value's root, dot separated; sequence elements as <c>[i]</c>.</param>
/// <param name="Depth">Nesting depth: 0 for the value's root.</param>
/// <param name="Kind">How the field compares.</param>
/// <param name="FieldA">Index into A's <see cref="DecodedValue.Fields"/>; -1 when only B has it.</param>
/// <param name="FieldB">Index into B's <see cref="DecodedValue.Fields"/>; -1 when only A has it.</param>
/// <param name="RangeA">A's bytes of the field; <see cref="PayloadRange.None"/> when only B has it.</param>
/// <param name="RangeB">B's bytes of the field; <see cref="PayloadRange.None"/> when only A has it.</param>
public readonly record struct FieldDiff(int Part, string Path, int Depth, FieldDiffKind Kind, int FieldA, int FieldB,
    PayloadRange RangeA, PayloadRange RangeB);
