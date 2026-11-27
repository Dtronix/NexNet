using NexNet.Inspector.Core.Model;

namespace NexNet.Inspector.Core.Findings;

/// <summary>
/// One thing the findings detectors noticed in a capture: how much it matters, what kind it is, a one-line message, the
/// stable IDs it is about (the text format's `#5`, `≋4·4`, `f18`, `@0x1BCB`, which the queries accept) and when.
/// </summary>
/// <param name="Severity">How much it matters.</param>
/// <param name="Kind">What it is about.</param>
/// <param name="Message">One line, without the IDs (the text form writes them before it).</param>
/// <param name="Ids">The stable IDs it cites, most relevant first; empty for a session-wide finding.</param>
/// <param name="Range">When, in the session's Stopwatch ticks; a finding about a moment has an equal start and end.</param>
public readonly record struct Finding(FindingSeverity Severity, FindingKind Kind, string Message, string[] Ids, TimeWindow Range)
{
    /// <summary>The session it was found in; for a finding across sessions, the first session it cites.</summary>
    public long SessionId { get; init; }

    /// <summary>Milliseconds from the session's start to <see cref="TimeWindow.Start"/> of <see cref="Range"/>.</summary>
    public double StartMs { get; init; }

    /// <summary>Milliseconds from the session's start to <see cref="TimeWindow.End"/> of <see cref="Range"/>.</summary>
    public double EndMs { get; init; }

    /// <summary>The IDs separated by spaces, as the text form writes them.</summary>
    public string IdText => Ids.Length == 0 ? "" : string.Join(' ', Ids);

    /// <summary>True when the finding cites <paramref name="id"/>.</summary>
    /// <param name="id">A stable ID (`#5`, `≋4·4`, `f18`, `@0x1BCB`).</param>
    public bool Cites(string id) => Array.IndexOf(Ids, id) >= 0;
}

/// <summary>Thresholds of the findings detectors; <see cref="Default"/> suits most captures.</summary>
public sealed record FindingsOptions
{
    /// <summary>The defaults.</summary>
    public static FindingsOptions Default { get; } = new();

    /// <summary>A completed call at least this long is slow whatever its method's usual duration (`--slow`).</summary>
    public double SlowCallMs { get; init; } = 1000;

    /// <summary>
    /// A call is a slow outlier when it took longer than every earlier call of its method (their p99), at least this many
    /// times their median, and at least <see cref="OutlierFloorMs"/>.
    /// </summary>
    public double OutlierFactor { get; init; } = 10;

    /// <summary>Calls shorter than this are never outliers (sub-millisecond noise).</summary>
    public double OutlierFloorMs { get; init; } = 20;

    /// <summary>Calls of a method needed before its outliers are judged.</summary>
    public int OutlierMinSamples { get; init; } = 8;

    /// <summary>A pipe writer or caller out of credit for at least this long stalled.</summary>
    public double StallMs { get; init; } = 50;

    /// <summary>An argument, result or channel item of at least this many bytes is large.</summary>
    public long LargeMessageBytes { get; init; } = 1024 * 1024;

    /// <summary>A message at least this fraction of the receiver's MaxMessageSize is near the limit.</summary>
    public double NearLimitFraction { get; init; } = 0.8;

    /// <summary>This many MessageChunk frames within <see cref="ChunkStormWindowMs"/> are a chunk storm.</summary>
    public int ChunkStormFrames { get; init; } = 128;

    /// <summary>The window of <see cref="ChunkStormFrames"/>.</summary>
    public double ChunkStormWindowMs { get; init; } = 1000;

    /// <summary>A ping round trip at least this long is an outlier whatever the session's usual round trip.</summary>
    public double PingRttMs { get; init; } = 500;

    /// <summary>A ping round trip at least this many times the median (and <see cref="PingRttFloorMs"/>) is an outlier.</summary>
    public double PingRttFactor { get; init; } = 10;

    /// <summary>Ping round trips shorter than this are never outliers.</summary>
    public double PingRttFloorMs { get; init; } = 50;

    /// <summary>Round trips needed before ping outliers are judged by the median.</summary>
    public int PingMinSamples { get; init; } = 8;

    /// <summary>Reconnections that make a reconnect loop.</summary>
    public int ReconnectLoopMin { get; init; } = 3;

    /// <summary>A session that starts within this long after a failed one from the same peer is a reconnection.</summary>
    public double ReconnectWithinMs { get; init; } = 10_000;

    /// <summary>Findings of one kind listed per session; the rest are counted in one "more" finding at the end.</summary>
    public int MaxPerKind { get; init; } = 20;
}
