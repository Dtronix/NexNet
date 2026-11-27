using NexNet.Inspector.Core.Format;

namespace NexNet.Inspector.Core.Assembly;

/// <summary>
/// What an attach snapshot says about a session that was already running: the state <see cref="MessageAssembler.Seed"/>
/// starts from.
/// </summary>
/// <param name="Pipes">Open pipes.</param>
/// <param name="Invocations">Pending-invocation counts and the large messages in transfer; null when the snapshot had none.</param>
/// <param name="Collections">Synchronized collections and the pipes that carry them.</param>
public sealed record SnapshotSeed(NntlPipeState[] Pipes, NntlInvocationState? Invocations, NntlCollectionState[] Collections)
{
    /// <summary>A seed with nothing in it.</summary>
    public static SnapshotSeed Empty { get; } = new([], null, []);

    /// <summary>
    /// Decodes the sections of a snapshot block. Unknown section kinds are skipped (later formats may add them) and a
    /// malformed section is left out, counted in <paramref name="malformedSections"/>: a snapshot only helps, so a bad
    /// one never stops a session from opening.
    /// </summary>
    /// <param name="snapshot">The snapshot block.</param>
    /// <param name="malformedSections">Sections that did not decode.</param>
    public static SnapshotSeed FromSnapshot(in NntlSnapshot snapshot, out int malformedSections)
    {
        malformedSections = 0;
        NntlPipeState[] pipes = [];
        NntlInvocationState? invocations = null;
        NntlCollectionState[] collections = [];
        foreach (var section in snapshot.Sections.Span)
        {
            try
            {
                switch (section.Kind)
                {
                    case NntlSnapshotSectionKind.OpenPipes:
                        pipes = NntlSnapshotSections.DecodeOpenPipes(section.Data);
                        break;
                    case NntlSnapshotSectionKind.PendingInvocations:
                        invocations = NntlSnapshotSections.DecodeInvocations(section.Data);
                        break;
                    case NntlSnapshotSectionKind.CollectionState:
                        collections = NntlSnapshotSections.DecodeCollections(section.Data);
                        break;
                }
            }
            catch (NntlFormatException)
            {
                malformedSections++;
            }
        }

        return new SnapshotSeed(pipes, invocations, collections);
    }
}
