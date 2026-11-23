namespace NexNet.Inspector.Core.Schema;

/// <summary>Limits of <see cref="SchemaDecoder"/>: what a hostile or huge value may cost to decode.</summary>
public sealed record SchemaDecoderOptions
{
    /// <summary>The defaults.</summary>
    public static SchemaDecoderOptions Default { get; } = new();

    /// <summary>
    /// Most fields one decode produces. Values past the limit are skipped whole as <see cref="FieldKind.Unknown"/> with
    /// <see cref="DecodeProblem.LimitReached"/>, so their bytes stay covered.
    /// </summary>
    public int MaxFields { get; init; } = 100_000;

    /// <summary>Deepest nesting decoded; deeper values are skipped the same way.</summary>
    public int MaxDepth { get; init; } = 64;
}
