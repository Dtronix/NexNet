using System.Globalization;
using System.Numerics;
using NexNet.Generator.Ir;

namespace NexNet.Generator.Targets.Java;

/// <summary>Java expressions for C# values: <c>default(T)</c> of a mapped type and member initializer literals.</summary>
internal static class JavaValues
{
    private const string Rt = JavaTarget.RuntimePackage + ".";

    /// <summary>
    /// C# <c>default(T)</c> as a Java expression of the mapped type, or null when Java's own default (<c>null</c>,
    /// <c>0</c>, <c>false</c>) is the same value. C# value types that map to Java classes (<c>decimal</c>,
    /// <c>DateTime</c>, <c>Guid</c>, enums, structs, value tuples...) get their zero value, so a default instance writes
    /// the same bytes as in C#.
    /// </summary>
    /// <param name="type">The type.</param>
    /// <param name="scope">The declared types.</param>
    /// <param name="name">The Java name of a declared type at the place the expression is written.</param>
    public static string? Default(TypeRef type, TypeScope scope, Func<TypeDeclIr, string> name)
    {
        switch (type.Kind)
        {
            case TypeRefKind.Prim:
                return PrimDefault(type.Prim);

            case TypeRefKind.Bin:
                return type.Clr is "Memory" or "ReadOnlyMemory" ? "new byte[0]" : null;

            case TypeRefKind.PrimArray:
            {
                if (type.Clr is not ("Memory" or "ReadOnlyMemory"))
                    return null;
                var array = JavaTarget.Instance.Types.Map(type, scope).Name;
                return array.EndsWith("[]", StringComparison.Ordinal) ? "new " + array.Substring(0, array.Length - 2) + "[0]" : null;
            }

            case TypeRefKind.Tuple when type.TupleKind == "KVP" && type.Args.Length == 2:
                return "new java.util.AbstractMap.SimpleImmutableEntry<>(" + Element(type.Args[0], scope, name) + ", "
                       + Element(type.Args[1], scope, name) + ")";

            case TypeRefKind.Tuple when type.TupleKind == "Value" && type.Args.Length is >= 1 and <= 7:
                return Rt + "Tuple" + JavaSyntax.Int(type.Args.Length) + ".of("
                       + string.Join(", ", type.Args.Select(a => Element(a, scope, name))) + ")";

            case TypeRefKind.Decl:
                switch (scope.Find(type.DeclId))
                {
                    case EnumDeclIr e:
                        return name(e) + ".of(" + EnumLiteral(0, e.Underlying) + ")";
                    case ObjectDeclIr { IsStruct: true, IsAbstract: false } o:
                        return "new " + name(o) + (o.TypeParams.Length > 0 ? "<>" : "") + "()";
                }

                return null;

            default:
                return null;
        }
    }

    /// <summary>C# <c>default(T)</c> in a position that needs an expression (a boxed tuple element).</summary>
    private static string Element(TypeRef type, TypeScope scope, Func<TypeDeclIr, string> name)
    {
        var value = Default(type, scope, name);
        if (value != null)
            return value;
        return type.Kind == TypeRefKind.Prim ? JavaSyntax.Zero(JavaTarget.Instance.Types.Map(type, scope).Name) : "null";
    }

    private static string? PrimDefault(PrimKind prim) => prim switch
    {
        PrimKind.Decimal => "java.math.BigDecimal.ZERO",
        PrimKind.DateTime => Rt + "NexusDateTime.of(0L, " + Rt + "NexusDateTimeKind.UNSPECIFIED)",
        PrimKind.DateTimeOffset => Rt + "NexusDateTimeOffset.of(0L, (short) 0)",
        PrimKind.TimeSpan => Rt + "NexusTimeSpan.of(0L)",
        PrimKind.DateOnly => Rt + "NexusDateOnly.of(0)",
        PrimKind.TimeOnly => Rt + "NexusTimeOnly.of(0L)",
        PrimKind.Guid => "new java.util.UUID(0L, 0L)",
        PrimKind.BigInteger => "java.math.BigInteger.ZERO",
        _ => null,
    };

    /// <summary>The Java type holding the value of an enum with this underlying type (as the primitive mapping).</summary>
    public static string EnumValueType(PrimKind underlying) => underlying switch
    {
        PrimKind.SByte => "byte",
        PrimKind.Int16 => "short",
        PrimKind.UInt32 or PrimKind.Int64 or PrimKind.UInt64 => "long",
        _ => "int",
    };

    /// <summary>A literal of the enum value type (raw 64 bits for UInt64, as the IR stores them).</summary>
    public static string EnumLiteral(long value, PrimKind underlying)
    {
        var text = JavaSyntax.Int(value);
        return EnumValueType(underlying) switch
        {
            "byte" => "(byte) " + text,
            "short" => "(short) " + text,
            "long" => text + "L",
            _ => text,
        };
    }

    /// <summary>
    /// The member's C# initializer as a Java expression of the mapped type, or null when there is none or it has no
    /// exact Java form (the member then starts at <see cref="Default"/>).
    /// </summary>
    public static string? Literal(LiteralIr? literal, TypeRef type, TypeScope scope, Func<TypeDeclIr, string> name, INameMapper names)
    {
        if (literal == null || literal.Kind == LiteralKind.Null || literal.Value == null)
            return null;

        var target = type.Kind == TypeRefKind.Nullable && type.Args.Length == 1 ? type.Args[0] : type;
        var value = literal.Value;

        if (literal.Kind == LiteralKind.Enum)
        {
            return target.Kind == TypeRefKind.Decl && scope.Find(target.DeclId) is EnumDeclIr e && e.Members.Any(m => m.Name == value)
                ? name(e) + "." + names.EnumMember(value)
                : null;
        }

        if (target.Kind != TypeRefKind.Prim)
            return null;

        var prim = target.Prim;
        switch (literal.Kind)
        {
            case LiteralKind.Bool:
                return prim == PrimKind.Bool && value is "true" or "false" ? value : null;

            case LiteralKind.String:
                return prim is PrimKind.String or PrimKind.Uri ? JavaSyntax.StringLiteral(value) : null;

            case LiteralKind.Char:
                if (value.Length != 1)
                    return null;
                if (prim == PrimKind.Char)
                    return JavaSyntax.CharLiteral(value[0]);
                return prim == PrimKind.String ? JavaSyntax.StringLiteral(value) : null;

            case LiteralKind.Integer:
                return BigInteger.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var integer)
                    ? Integer(integer, prim, value)
                    : null;

            case LiteralKind.Real:
            case LiteralKind.Decimal:
                return Real(value, prim, literal.Kind == LiteralKind.Decimal);

            default:
                return null;
        }
    }

    private static string? Integer(BigInteger value, PrimKind prim, string text)
    {
        bool InRange(long min, long max) => value >= min && value <= max;

        switch (prim)
        {
            case PrimKind.SByte:
                return InRange(sbyte.MinValue, sbyte.MaxValue) ? "(byte) " + text : null;
            case PrimKind.Byte:
                return InRange(byte.MinValue, byte.MaxValue) ? text : null;
            case PrimKind.Int16:
                return InRange(short.MinValue, short.MaxValue) ? "(short) " + text : null;
            case PrimKind.UInt16:
                return InRange(ushort.MinValue, ushort.MaxValue) ? text : null;
            case PrimKind.Int32:
                return InRange(int.MinValue, int.MaxValue) ? text : null;
            case PrimKind.UInt32:
                return InRange(uint.MinValue, uint.MaxValue) ? text + "L" : null;
            case PrimKind.Int64:
                return InRange(long.MinValue, long.MaxValue) ? text + "L" : null;
            case PrimKind.UInt64:
                if (value < 0 || value > ulong.MaxValue)
                    return null;
                return JavaSyntax.Int(unchecked((long)(ulong)value)) + "L";
            case PrimKind.Single:
            case PrimKind.Half:
                return text + "f";
            case PrimKind.Double:
                return text + "d";
            case PrimKind.Char:
                return InRange(char.MinValue, char.MaxValue) ? "(char) " + text : null;
            case PrimKind.Decimal:
                return "new java.math.BigDecimal(" + JavaSyntax.StringLiteral(text) + ")";
            default:
                return null;
        }
    }

    private static string? Real(string text, PrimKind prim, bool isDecimal)
    {
        switch (prim)
        {
            case PrimKind.Decimal:
                return decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out _)
                    ? "new java.math.BigDecimal(" + JavaSyntax.StringLiteral(text) + ")"
                    : null;
            case PrimKind.Single:
            case PrimKind.Half:
                return isDecimal ? null : Floating(text, "Float", "f");
            case PrimKind.Double:
                return isDecimal ? null : Floating(text, "Double", "d");
            default:
                return null;
        }
    }

    private static string? Floating(string text, string box, string suffix)
    {
        switch (text)
        {
            case "NaN":
                return box + ".NaN";
            case "Infinity":
            case "∞":
                return box + ".POSITIVE_INFINITY";
            case "-Infinity":
            case "-∞":
                return box + ".NEGATIVE_INFINITY";
        }

        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out _) ? text + suffix : null;
    }
}
