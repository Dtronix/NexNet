using NexNet.Inspector.Core.Model;

namespace NexNet.Inspector.Core.Text;

/// <summary>
/// One rendered line before it is written: what it is, when, which way, its stable ID and its condensed words. The text
/// and ndjson forms are both written from it (<see cref="TextRecordWriter"/>); filters (<see cref="TextFilter.Match"/>)
/// test it; queries and findings build on the same records.
/// </summary>
public readonly record struct TextRecord
{
    /// <summary>What the line says.</summary>
    public TextRecordKind Kind { get; init; }

    /// <summary>Stopwatch ticks of its first event.</summary>
    public long Ticks { get; init; }

    /// <summary>Stopwatch ticks of its last event (a collapsed run's end); equal to <see cref="Ticks"/> otherwise.</summary>
    public long LastTicks { get; init; }

    /// <summary>Milliseconds from the session's start to <see cref="Ticks"/>.</summary>
    public double TimeMs { get; init; }

    /// <summary>Milliseconds from <see cref="Ticks"/> to <see cref="LastTicks"/>.</summary>
    public double SpanMs { get; init; }

    /// <summary>Which way, seen from the tapped endpoint; null for undirected lines.</summary>
    public TapDirection? Direction { get; init; }

    /// <summary>The stable ID: `#5` (invocation), `≋4·4` (pipe), `f18` (frame), `@0x1BCB` (session).</summary>
    public string? Id { get; init; }

    /// <summary>For collapsed calls, the last call's ID.</summary>
    public string? LastId { get; init; }

    /// <summary>A method, item type, collection, frame type, or the end's initiating side.</summary>
    public string? Name { get; init; }

    /// <summary>The condensed words: arguments, an item's value, a pipe event, a frame's target.</summary>
    public string? Value { get; init; }

    /// <summary>An outcome: `ok`, `exception`, `canceled`, `…`; a frame's flag letters; the end's reason.</summary>
    public string? Outcome { get; init; }

    /// <summary>A call's result value or exception.</summary>
    public string? Result { get; init; }

    /// <summary>A call's duration in milliseconds; null when not known.</summary>
    public double? DurationMs { get; init; }

    /// <summary>Events collapsed into this line; 1 for a single one.</summary>
    public int Count { get; init; }

    /// <summary>Bytes the line covers (a message, data, a gap, a frame); -1 when it says none.</summary>
    public long Bytes { get; init; }

    /// <summary>Index into the session's frame order of its first frame; -1 for none.</summary>
    public int Frame { get; init; }

    /// <summary>Index of its invocation, pipe or channel item in the assembler; -1 for none.</summary>
    public int Entity { get; init; }

    // Collapse key: records with equal keys merge into one ×N line.
    internal string? Key { get; init; }

    // Where the record started: the cursor fields of its first event and its ordinal among that event's records.
    internal byte Phase { get; init; }

    internal int EventFrame { get; init; }

    internal int EventGap { get; init; }

    internal int Sub { get; init; }

    /// <summary>The cursor that resumes a rendering at this record.</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="level">The level rendered.</param>
    /// <param name="filter">The filter's fingerprint.</param>
    public TextCursor CursorAt(long sessionId, TextLevel level, ulong filter) => new(sessionId, level, Phase, EventFrame, EventGap, Sub, filter);
}
