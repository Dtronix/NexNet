namespace NexNet.Generator.MemoryPack;

// should synchronize with MemoryPack.Core.Attributes.cs GenerateType
internal enum GenerateType
{
    Object,
    VersionTolerant,
    CircularReference,
    Collection,
    NoGenerate,

    // only used in Generator
    Union
}

internal enum SerializeLayout
{
    Sequential, // default
    Explicit
}
