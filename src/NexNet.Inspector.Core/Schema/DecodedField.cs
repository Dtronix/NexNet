namespace NexNet.Inspector.Core.Schema;

/// <summary>
/// A decoded scalar, without allocation: 128 bits whose meaning the field's <see cref="FieldKind"/> gives. Strings and
/// bytes are not copied; they are the field's content range, turned into text by <see cref="DecodedValue"/> on demand.
/// </summary>
/// <param name="Bits">The main 64 bits.</param>
/// <param name="Extra">The second 64 bits (decimal hi and flags, DateTimeOffset offset, primitive array element kind).</param>
public readonly record struct DecodedScalar(long Bits, long Extra)
{
    /// <summary>As a signed integer.</summary>
    public long Int64 => Bits;

    /// <summary>As an unsigned integer.</summary>
    public ulong UInt64 => (ulong)Bits;

    /// <summary>As a boolean.</summary>
    public bool Boolean => Bits != 0;

    /// <summary>As a <c>float</c> (<see cref="FieldKind.Single"/>).</summary>
    public float Single => BitConverter.Int32BitsToSingle((int)Bits);

    /// <summary>As a <c>double</c> (<see cref="FieldKind.Double"/>).</summary>
    public double Double => BitConverter.Int64BitsToDouble(Bits);

    /// <summary>As a <c>Half</c> (<see cref="FieldKind.Half"/>).</summary>
    public Half Half => BitConverter.UInt16BitsToHalf((ushort)Bits);

    /// <summary>As a <c>char</c>.</summary>
    public char Char => (char)Bits;
}

/// <summary>
/// One node of a decoded value tree. Fields are stored flat in pre-order in <see cref="DecodedValue.Fields"/> and
/// linked by index; byte ranges are relative to <see cref="DecodedValue.Bytes"/> (see <see cref="DecodedValue.GetRange"/>
/// for the message coordinates).
/// </summary>
public struct DecodedField
{
    /// <summary>Position in <see cref="DecodedValue.Fields"/>.</summary>
    public int Index { readonly get; internal set; }

    /// <summary>The parent field; -1 for a top-level field.</summary>
    public int Parent { readonly get; internal set; }

    /// <summary>The first child; -1 when none.</summary>
    public int FirstChild { readonly get; internal set; }

    /// <summary>The next field with the same parent; -1 when none.</summary>
    public int NextSibling { readonly get; internal set; }

    /// <summary>Number of children.</summary>
    public int ChildCount { readonly get; internal set; }

    /// <summary>Nesting depth: 0 for a top-level field.</summary>
    public int Depth { readonly get; internal set; }

    /// <summary>What the field is.</summary>
    public FieldKind Kind { readonly get; internal set; }

    /// <summary>What went wrong at this field, if anything.</summary>
    public DecodeProblem Problem { readonly get; internal set; }

    /// <summary>
    /// The schema's name for the field: a parameter, member, method or case name, or a fixed label (Key, Value, Item1,
    /// Flags…). Null for sequence elements and unknown positions; see <see cref="ElementIndex"/>.
    /// </summary>
    public string? Name { readonly get; internal set; }

    /// <summary>Position in the parent array (element, member key, argument, entry); -1 when not in an array.</summary>
    public int ElementIndex { readonly get; internal set; }

    /// <summary>The schema type the field was decoded as; null when unknown.</summary>
    public SchemaTypeRef? Type { readonly get; internal set; }

    /// <summary>The declared type of an object, union case or enum; null otherwise.</summary>
    public SchemaTypeDecl? Decl { readonly get; internal set; }

    /// <summary>Offset of the field's first byte in <see cref="DecodedValue.Bytes"/>.</summary>
    public int Offset { readonly get; internal set; }

    /// <summary>Bytes of the field, headers and children included.</summary>
    public int Length { readonly get; internal set; }

    /// <summary>Bytes before the content: the str, bin, ext or container header.</summary>
    public int HeaderLength { readonly get; internal set; }

    /// <summary>The scalar value; see <see cref="FieldKind"/> for its meaning.</summary>
    public DecodedScalar Value { readonly get; internal set; }

    /// <summary>Offset of the content (string or binary bytes, primitive array elements, container elements).</summary>
    public readonly int ContentOffset => Offset + HeaderLength;

    /// <summary>Bytes of the content.</summary>
    public readonly int ContentLength => Length - HeaderLength;

    /// <summary>One past the field's last byte.</summary>
    public readonly int End => Offset + Length;
}
