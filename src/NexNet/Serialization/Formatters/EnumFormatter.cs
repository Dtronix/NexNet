using System;
using System.Runtime.CompilerServices;

namespace NexNet.Serialization.Formatters;

/// <summary>
/// Writes an enum as its underlying integer (compact encoding).
/// </summary>
/// <typeparam name="T">The enum type.</typeparam>
public sealed class EnumFormatter<T> : NexusFormatter<T>
    where T : struct, Enum
{
    /// <summary>Shared instance.</summary>
    public static readonly EnumFormatter<T> Instance = new();

    private static readonly TypeCode Underlying = Type.GetTypeCode(Enum.GetUnderlyingType(typeof(T)));

    /// <inheritdoc />
    public override void Serialize(ref MsgPackWriter writer, T value)
    {
        switch (Underlying)
        {
            case TypeCode.SByte: writer.Write(Unsafe.As<T, sbyte>(ref value)); break;
            case TypeCode.Byte: writer.Write(Unsafe.As<T, byte>(ref value)); break;
            case TypeCode.Int16: writer.Write(Unsafe.As<T, short>(ref value)); break;
            case TypeCode.UInt16: writer.Write(Unsafe.As<T, ushort>(ref value)); break;
            case TypeCode.Int32: writer.Write(Unsafe.As<T, int>(ref value)); break;
            case TypeCode.UInt32: writer.Write(Unsafe.As<T, uint>(ref value)); break;
            case TypeCode.Int64: writer.Write(Unsafe.As<T, long>(ref value)); break;
            case TypeCode.UInt64: writer.Write(Unsafe.As<T, ulong>(ref value)); break;
            default: throw new NotSupportedException($"Unsupported enum underlying type for {typeof(T)}.");
        }
    }

    /// <inheritdoc />
    public override void Deserialize(ref MsgPackReader reader, ref T value)
    {
        switch (Underlying)
        {
            case TypeCode.SByte: { var v = reader.ReadSByte(); value = Unsafe.As<sbyte, T>(ref v); break; }
            case TypeCode.Byte: { var v = reader.ReadByte(); value = Unsafe.As<byte, T>(ref v); break; }
            case TypeCode.Int16: { var v = reader.ReadInt16(); value = Unsafe.As<short, T>(ref v); break; }
            case TypeCode.UInt16: { var v = reader.ReadUInt16(); value = Unsafe.As<ushort, T>(ref v); break; }
            case TypeCode.Int32: { var v = reader.ReadInt32(); value = Unsafe.As<int, T>(ref v); break; }
            case TypeCode.UInt32: { var v = reader.ReadUInt32(); value = Unsafe.As<uint, T>(ref v); break; }
            case TypeCode.Int64: { var v = reader.ReadInt64(); value = Unsafe.As<long, T>(ref v); break; }
            case TypeCode.UInt64: { var v = reader.ReadUInt64(); value = Unsafe.As<ulong, T>(ref v); break; }
            default: throw new NotSupportedException($"Unsupported enum underlying type for {typeof(T)}.");
        }
    }
}
