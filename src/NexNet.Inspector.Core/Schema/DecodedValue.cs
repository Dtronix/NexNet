using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using NexNet.Inspector.Core.Assembly;

namespace NexNet.Inspector.Core.Schema;

/// <summary>
/// The result of decoding one value: its bytes and a flat, pre-order tree of <see cref="DecodedField"/>s with exact
/// byte ranges. Text is never produced while decoding; <see cref="GetText"/> and <see cref="FormatValue"/> render a
/// field on demand.
/// </summary>
public sealed class DecodedValue
{
    private readonly List<DecodedField> _fields;

    internal DecodedValue(PayloadRange range, ReadOnlyMemory<byte> bytes, List<DecodedField> fields)
    {
        Range = range;
        Bytes = bytes;
        _fields = fields;
        foreach (var field in fields)
        {
            if (field.Problem != DecodeProblem.None)
            {
                Problem = field.Problem;
                break;
            }
        }
    }

    /// <summary>
    /// Where <see cref="Bytes"/> came from, in message coordinates; <see cref="PayloadRange.None"/> for bytes decoded
    /// on their own. Its length may be shorter than the row's range when the message was truncated.
    /// </summary>
    public PayloadRange Range { get; }

    /// <summary>The decoded bytes. Field offsets are relative to their start.</summary>
    public ReadOnlyMemory<byte> Bytes { get; }

    /// <summary>The fields in pre-order: a parent comes before its children. Top-level fields have parent -1.</summary>
    public ReadOnlySpan<DecodedField> Fields => CollectionsMarshal.AsSpan(_fields);

    /// <summary>The first problem found in pre-order; <see cref="DecodeProblem.None"/> when every byte decoded.</summary>
    public DecodeProblem Problem { get; }

    /// <summary>True when the whole value is one raw field.</summary>
    public bool IsRaw => _fields.Count == 1 && _fields[0].Kind == FieldKind.Raw;

    /// <summary>The bytes of a field, headers included.</summary>
    /// <param name="field">A field of this value.</param>
    public ReadOnlySpan<byte> GetBytes(in DecodedField field) => Bytes.Span.Slice(field.Offset, field.Length);

    /// <summary>The content of a field: string or binary bytes, primitive array elements, container elements.</summary>
    /// <param name="field">A field of this value.</param>
    public ReadOnlySpan<byte> GetContent(in DecodedField field) => Bytes.Span.Slice(field.ContentOffset, field.ContentLength);

    /// <summary>A field's bytes in message coordinates, for the hex view (see <see cref="MessageAssembler.GetFragments"/>).</summary>
    /// <param name="field">A field of this value.</param>
    public PayloadRange GetRange(in DecodedField field) => new(Range.Message, Range.Offset + field.Offset, field.Length);

    /// <summary>The deepest field that covers a byte, for linking a hex selection back to the tree.</summary>
    /// <param name="offset">Offset in <see cref="Bytes"/>.</param>
    /// <returns>The field index, or -1 when no field covers it.</returns>
    public int FindField(int offset)
    {
        var fields = Fields;
        var found = -1;
        var candidate = fields.Length > 0 ? 0 : -1;
        while (candidate >= 0)
        {
            ref readonly var field = ref fields[candidate];
            if (offset >= field.Offset && offset < field.End)
            {
                found = candidate;
                candidate = field.FirstChild;
            }
            else
            {
                candidate = field.NextSibling;
            }
        }

        return found;
    }

    /// <summary>The text of a string, URI or version field; null for nil and other kinds.</summary>
    /// <param name="field">A field of this value.</param>
    /// <remarks>Invalid UTF-8 shows as U+FFFD: the inspector displays what was sent rather than refusing it.</remarks>
    public string? GetText(in DecodedField field)
        => field.Kind is FieldKind.String or FieldKind.Uri or FieldKind.Version ? Encoding.UTF8.GetString(GetContent(field)) : null;

    /// <summary>Reads one element of a primitive array field.</summary>
    /// <param name="field">A <see cref="FieldKind.PrimitiveArray"/> field.</param>
    /// <param name="index">The element index.</param>
    /// <param name="value">
    /// The element, in the representation of the matching scalar <see cref="FieldKind"/> (Integer, UnsignedInteger,
    /// Single, Double, Half or Char bits).
    /// </param>
    /// <returns>False for another kind of field or an index out of range.</returns>
    public bool TryGetElement(in DecodedField field, int index, out DecodedScalar value)
    {
        value = default;
        if (field.Kind != FieldKind.PrimitiveArray || (ulong)index >= (ulong)field.Value.Int64)
            return false;

        var kind = (byte)field.Value.Extra;
        var size = PrimitiveArrays.ElementSize(kind);
        var data = GetContent(field).Slice(index * size, size);
        long bits = kind switch
        {
            1 => (sbyte)data[0],
            2 => BinaryPrimitives.ReadInt16LittleEndian(data),
            3 or 10 or 11 => BinaryPrimitives.ReadUInt16LittleEndian(data),
            4 => BinaryPrimitives.ReadInt32LittleEndian(data),
            5 => BinaryPrimitives.ReadUInt32LittleEndian(data),
            8 => BinaryPrimitives.ReadInt32LittleEndian(data),
            _ => BinaryPrimitives.ReadInt64LittleEndian(data),
        };
        value = new DecodedScalar(bits, 0);
        return true;
    }

    /// <summary>The <see cref="FieldKind"/> of the elements of a primitive array field.</summary>
    /// <param name="field">A <see cref="FieldKind.PrimitiveArray"/> field.</param>
    public static FieldKind GetElementKind(in DecodedField field) => (byte)field.Value.Extra switch
    {
        1 or 2 or 4 or 6 => FieldKind.Integer,
        3 or 5 or 7 => FieldKind.UnsignedInteger,
        8 => FieldKind.Single,
        9 => FieldKind.Double,
        10 => FieldKind.Char,
        11 => FieldKind.Half,
        _ => FieldKind.Unknown,
    };

    /// <summary>Renders a field's value as display text (invariant culture). Allocates; call it only for visible rows.</summary>
    /// <param name="field">A field of this value.</param>
    public string FormatValue(in DecodedField field)
    {
        var value = field.Value;
        var invariant = CultureInfo.InvariantCulture;
        switch (field.Kind)
        {
            case FieldKind.Nil:
                return "null";
            case FieldKind.Boolean:
                return value.Boolean ? "true" : "false";
            case FieldKind.Integer:
            case FieldKind.PipeId:
            case FieldKind.UnionTag:
                return value.Int64.ToString(invariant);
            case FieldKind.UnsignedInteger:
                return value.UInt64.ToString(invariant);
            case FieldKind.Single:
                return value.Single.ToString("R", invariant);
            case FieldKind.Double:
                return value.Double.ToString("R", invariant);
            case FieldKind.Half:
                return value.Half.ToString(invariant);
            case FieldKind.Char:
                return char.IsControl(value.Char) || char.IsSurrogate(value.Char)
                    ? $"U+{(int)value.Char:X4}"
                    : $"'{value.Char}' U+{(int)value.Char:X4}";
            case FieldKind.String:
            case FieldKind.Uri:
            case FieldKind.Version:
                return GetText(field) ?? "";
            case FieldKind.Binary:
                return FormatBytes(GetContent(field));
            case FieldKind.Decimal:
                return FormatDecimal(value);
            case FieldKind.DateTime:
                return FormatDateTime(value.Int64);
            case FieldKind.DateTimeOffset:
                return FormatDateTimeOffset(value.Int64, value.Extra);
            case FieldKind.TimeSpan:
                return TimeSpan.FromTicks(value.Int64).ToString("c", invariant);
            case FieldKind.DateOnly:
                return value.Int64 is >= 0 and <= 3_652_058
                    ? DateOnly.FromDayNumber((int)value.Int64).ToString("O", invariant)
                    : $"day {value.Int64.ToString(invariant)} (out of range)";
            case FieldKind.TimeOnly:
                return value.Int64 is >= 0 and < TimeSpan.TicksPerDay
                    ? new TimeOnly(value.Int64).ToString("O", invariant)
                    : $"{value.Int64.ToString(invariant)} ticks (out of range)";
            case FieldKind.Guid:
                return new Guid(GetContent(field), bigEndian: true).ToString("D");
            case FieldKind.BigInteger:
                return new BigInteger(GetContent(field)).ToString(invariant);
            case FieldKind.Enum:
                return FormatEnum(field.Decl as SchemaEnumDecl, value.Int64);
            case FieldKind.Object:
            case FieldKind.Union:
                return field.Decl?.Name ?? field.Name ?? "";
            case FieldKind.Sequence:
            case FieldKind.Map:
            case FieldKind.Tuple:
                return $"[{field.ChildCount.ToString(invariant)}]";
            case FieldKind.PrimitiveArray:
                return $"{GetElementKind(field)}[{value.Int64.ToString(invariant)}]";
            case FieldKind.Arguments:
                return $"{field.Name}({field.ChildCount.ToString(invariant)})";
            case FieldKind.CollectionMessage:
            case FieldKind.CollectionBody:
                return field.Name ?? $"tag {value.Int64.ToString(invariant)}";
            default:
                return $"{field.Length.ToString(invariant)} bytes";
        }
    }

    private static string FormatBytes(ReadOnlySpan<byte> bytes)
    {
        const int preview = 32;
        var shown = Convert.ToHexString(bytes.Slice(0, Math.Min(bytes.Length, preview)));
        return bytes.Length <= preview ? shown : $"{shown}… ({bytes.Length.ToString(CultureInfo.InvariantCulture)} bytes)";
    }

    private static string FormatDecimal(DecodedScalar value)
    {
        var lo = (int)value.Int64;
        var mid = (int)(value.Int64 >> 32);
        var hi = (int)value.Extra;
        var flags = (int)(value.Extra >> 32);
        var scale = (flags >> 16) & 0xff;
        // Spec §10.6: reserved bits must be zero and the scale at most 28.
        if ((flags & 0x7F00FFFF) != 0 || scale > 28)
            return "invalid decimal";
        return new decimal(lo, mid, hi, flags < 0, (byte)scale).ToString(CultureInfo.InvariantCulture);
    }

    private static string FormatDateTime(long binary)
    {
        try
        {
            return DateTime.FromBinary(binary).ToString("O", CultureInfo.InvariantCulture);
        }
        catch (ArgumentException)
        {
            return $"{binary.ToString(CultureInfo.InvariantCulture)} (out of range)";
        }
    }

    private static string FormatDateTimeOffset(long utcTicks, long offsetMinutes)
    {
        try
        {
            var offset = TimeSpan.FromMinutes(offsetMinutes);
            return new DateTimeOffset(new DateTime(utcTicks, DateTimeKind.Utc)).ToOffset(offset).ToString("O", CultureInfo.InvariantCulture);
        }
        catch (ArgumentException)
        {
            return $"[{utcTicks.ToString(CultureInfo.InvariantCulture)}, {offsetMinutes.ToString(CultureInfo.InvariantCulture)}] (out of range)";
        }
    }

    private static string FormatEnum(SchemaEnumDecl? decl, long value)
    {
        var number = decl is { IsUnsigned: true } ? ((ulong)value).ToString(CultureInfo.InvariantCulture) : value.ToString(CultureInfo.InvariantCulture);
        if (decl is null)
            return number;
        if (decl.TryGetName(value, out var name))
            return name;
        if (!decl.IsFlags || value == 0)
            return number;

        // A flags value that is not a single member: the members whose bits it holds, if they cover it exactly.
        var builder = new StringBuilder();
        long covered = 0;
        foreach (var member in decl.Members)
        {
            if (member.Value == 0 || (value & member.Value) != member.Value || (covered & member.Value) == member.Value)
                continue;
            if (builder.Length > 0)
                builder.Append(" | ");
            builder.Append(member.Name);
            covered |= member.Value;
        }

        return covered == value ? builder.ToString() : number;
    }
}
