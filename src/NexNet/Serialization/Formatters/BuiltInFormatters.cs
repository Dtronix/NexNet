using System;
using System.Buffers;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using NexNet.Serialization.Formatters;

namespace NexNet.Serialization;

/// <summary>
/// Registers the built-in formatters.
/// </summary>
internal static class BuiltInFormatters
{
    public static void RegisterAll()
    {
        // Scalars
        RegisterStruct(BooleanFormatter.Instance);
        RegisterStruct(ByteFormatter.Instance);
        RegisterStruct(SByteFormatter.Instance);
        RegisterStruct(Int16Formatter.Instance);
        RegisterStruct(UInt16Formatter.Instance);
        RegisterStruct(Int32Formatter.Instance);
        RegisterStruct(UInt32Formatter.Instance);
        RegisterStruct(Int64Formatter.Instance);
        RegisterStruct(UInt64Formatter.Instance);
        RegisterStruct(SingleFormatter.Instance);
        RegisterStruct(DoubleFormatter.Instance);
        RegisterStruct(HalfFormatter.Instance);
        RegisterStruct(CharFormatter.Instance);
        RegisterStruct(DateTimeFormatter.Instance);
        RegisterStruct(DateTimeOffsetFormatter.Instance);
        RegisterStruct(TimeSpanFormatter.Instance);
        RegisterStruct(DateOnlyFormatter.Instance);
        RegisterStruct(TimeOnlyFormatter.Instance);
        RegisterStruct(GuidFormatter.Instance);
        RegisterStruct(DecimalFormatter.Instance);
        RegisterStruct(BigIntegerFormatter.Instance);
        NexusFormatterRegistry.Register(StringFormatter.Instance);
        NexusFormatterRegistry.Register(UriFormatter.Instance);
        NexusFormatterRegistry.Register(VersionFormatter.Instance);

        // Binary
        NexusFormatterRegistry.Register(ByteArrayFormatter.Instance);
        NexusFormatterRegistry.Register(MemoryByteFormatter.Instance);
        NexusFormatterRegistry.Register(ReadOnlyMemoryByteFormatter.Instance);
        NexusFormatterRegistry.Register(ArraySegmentByteFormatter.Instance);
        NexusFormatterRegistry.Register(ReadOnlySequenceByteFormatter.Instance);

        // Primitive arrays/lists/memory (ext 78)
        RegisterPrimitive<sbyte>();
        RegisterPrimitive<short>();
        RegisterPrimitive<ushort>();
        RegisterPrimitive<int>();
        RegisterPrimitive<uint>();
        RegisterPrimitive<long>();
        RegisterPrimitive<ulong>();
        RegisterPrimitive<float>();
        RegisterPrimitive<double>();
        RegisterPrimitive<char>();
        RegisterPrimitive<Half>();

        // Common element-wise collections
        RegisterSequences<bool>();
        RegisterSequences<string>();
        RegisterSequences<Guid>();
        RegisterSequences<DateTime>();
        RegisterSequences<decimal>();
        NexusFormatterRegistry.Register(new ListFormatter<byte>());
        NexusFormatterRegistry.Register(new DictionaryFormatter<string, string>());
        NexusFormatterRegistry.Register(new DictionaryFormatter<string, int>());
        NexusFormatterRegistry.Register(new DictionaryFormatter<int, string>());

        // Collection connection arguments (a single pipe ID).
        NexusFormatterRegistry.Register(new ValueTupleFormatter<byte>());
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RegisterStruct<T>(NexusFormatter<T> formatter)
        where T : struct
    {
        NexusFormatterRegistry.Register(formatter);
        NexusFormatterRegistry.Register<T?>(new NullableFormatter<T>(formatter));
    }

    private static void RegisterPrimitive<T>()
        where T : unmanaged
    {
        NexusFormatterRegistry.Register(PrimitiveArrayFormatter<T>.Instance);
        NexusFormatterRegistry.Register(PrimitiveListFormatter<T>.Instance);
        NexusFormatterRegistry.Register(PrimitiveMemoryFormatter<T>.Instance);
        NexusFormatterRegistry.Register(PrimitiveReadOnlyMemoryFormatter<T>.Instance);
    }

    private static void RegisterSequences<T>()
    {
        NexusFormatterRegistry.Register(new ArrayFormatter<T>());
        NexusFormatterRegistry.Register(new ListFormatter<T>());
        NexusFormatterRegistry.Register(new InterfaceListFormatter<IList<T>, T>());
        NexusFormatterRegistry.Register(new InterfaceListFormatter<IReadOnlyList<T>, T>());
        NexusFormatterRegistry.Register(new InterfaceListFormatter<IEnumerable<T>, T>());
        NexusFormatterRegistry.Register(new InterfaceListFormatter<ICollection<T>, T>());
        NexusFormatterRegistry.Register(new InterfaceListFormatter<IReadOnlyCollection<T>, T>());
    }
}
