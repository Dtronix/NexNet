namespace NexNet.Inspector.Core.Compare;

/// <summary>
/// An alignment computed by <see cref="CompareSession.PrepareAlignment"/> and not yet applied: the clock, the entities
/// with their re-timed differences, the groups' counts and the Δ list. Computing it off the UI thread and swapping it in
/// with <see cref="CompareSession.ApplyAlignment"/> keeps a re-alignment of a large compare from blocking the UI.
/// </summary>
public sealed class CompareAlignment
{
    internal CompareAlignment(CompareSession owner, int union, CompareClock clock, AlignmentEvent alignmentEvent, long duration,
        List<EntityMatch> matches, List<EntityGroupRow> groups, DeltaList deltas)
    {
        Owner = owner;
        Union = union;
        Clock = clock;
        Event = alignmentEvent;
        Duration = duration;
        Matches = matches;
        Groups = groups;
        Deltas = deltas;
    }

    /// <summary>The alignment.</summary>
    public Alignment Mode => Clock.Mode;

    /// <summary>The event of an <see cref="Alignment.OnEvent"/> alignment.</summary>
    public AlignmentEvent Event { get; }

    /// <summary>The timeline it puts both documents on.</summary>
    public CompareClock Clock { get; }

    internal CompareSession Owner { get; }

    internal int Union { get; }

    internal long Duration { get; }

    internal List<EntityMatch> Matches { get; }

    internal List<EntityGroupRow> Groups { get; }

    internal DeltaList Deltas { get; }
}
