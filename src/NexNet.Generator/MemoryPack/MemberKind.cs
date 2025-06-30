namespace NexNet.Generator.MemoryPack;

internal enum MemberKind
{
    MemoryPackable, // IMemoryPackable<> or [MemoryPackable]
    Unmanaged,
    Nullable,
    UnmanagedNullable,
    KnownType,
    String,
    Array,
    UnmanagedArray,
    MemoryPackableArray, // T[] where T: IMemoryPackable<T>
    MemoryPackableList, // List<T> where T: IMemoryPackable<T>
    MemoryPackableCollection, // GenerateType.Collection
    MemoryPackableNoGenerate, // GenerateType.NoGenerate
    MemoryPackableUnion,
    Enum,

    // from attribute
    AllowSerialize,
    MemoryPackUnion,

    Object, // others allow
    RefLike, // not allowed
    NonSerializable, // not allowed
    Blank, // blank marker
    CustomFormatter, // used [MemoryPackCustomFormatterAttribtue]
}
