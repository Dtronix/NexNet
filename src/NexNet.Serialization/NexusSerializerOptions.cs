namespace NexNet.Serialization;

/// <summary>
/// Trust level of data being deserialized.
/// </summary>
public enum SerializerSecurity
{
    /// <summary>
    /// Data may come from a hostile peer. Enables strict UTF-8, depth limits and hash-flooding resistant collections.
    /// </summary>
    Untrusted = 0,

    /// <summary>
    /// Data comes from a trusted peer. Disables the depth limit, strict UTF-8 and randomized collection comparers.
    /// </summary>
    Trusted = 1,
}

/// <summary>
/// Options applied when deserializing MessagePack data.
/// Length checks before allocation, non-recursive skipping and exact message shapes are always enforced.
/// </summary>
public sealed record NexusSerializerOptions
{
    /// <summary>
    /// Default options for data from untrusted peers.
    /// </summary>
    public static readonly NexusSerializerOptions Untrusted = new();

    /// <summary>
    /// Options for data from trusted peers.
    /// </summary>
    public static readonly NexusSerializerOptions Trusted = new()
    {
        Security = SerializerSecurity.Trusted,
        StrictUtf8 = false,
        MaxDepth = int.MaxValue
    };

    /// <summary>
    /// Trust level. Defaults to <see cref="SerializerSecurity.Untrusted"/>.
    /// </summary>
    public SerializerSecurity Security { get; init; } = SerializerSecurity.Untrusted;

    /// <summary>
    /// Maximum nesting depth of containers. Defaults to 64.
    /// </summary>
    public int MaxDepth { get; init; } = 64;

    /// <summary>
    /// When true, invalid UTF-8 in strings throws instead of being replaced. Defaults to true.
    /// </summary>
    public bool StrictUtf8 { get; init; } = true;

    /// <summary>
    /// Maximum number of bytes a single channel item may buffer before it is considered hostile. Defaults to 16 MiB.
    /// </summary>
    public int MaxBufferedItemSize { get; init; } = 16 * 1024 * 1024;

    /// <summary>
    /// True when dictionaries and sets with non-string keys use randomized hash comparers.
    /// </summary>
    public bool HashFloodingResistantCollections => Security == SerializerSecurity.Untrusted;
}
