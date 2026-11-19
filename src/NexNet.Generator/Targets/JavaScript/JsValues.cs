using System.Globalization;
using System.Numerics;
using NexNet.Generator.Ir;

namespace NexNet.Generator.Targets.JavaScript;

/// <summary>JavaScript expressions for C# values: <c>default(T)</c> of a mapped type and member initializer literals.</summary>
internal static class JsValues
{
    private const string Rt = JsSyntax.Rt + ".";

    /// <summary>
    /// C# <c>default(T)</c> as a JavaScript expression, so a default instance writes the same bytes as in C#: 0, false,
    /// <c>0n</c>, the zero value types, empty arrays for <c>Memory&lt;T&gt;</c>, a new struct, tuples of defaults; <c>null</c>
    /// for reference types and type parameters.
    /// </summary>
    /// <param name="type">The type.</param>
    /// <param name="scope">The declared types.</param>
    /// <param name="name">The JavaScript name of a declared type where the expression is written.</param>
    public static string Default(TypeRef type, TypeScope scope, Func<TypeDeclIr, string> name)
    {
        switch (type.Kind)
        {
            case TypeRefKind.Prim:
                return PrimDefault(type.Prim);

            case TypeRefKind.Bin:
                return type.Clr is "Memory" or "ReadOnlyMemory" ? "new Uint8Array(0)" : "null";

            case TypeRefKind.PrimArray:
            {
                if (type.Clr is not ("Memory" or "ReadOnlyMemory"))
                    return "null";
                var array = JavaScriptTarget.Instance.Types.Map(type, scope);
                return array.IsSupported ? "new " + array.Name + "(0)" : "null";
            }

            case TypeRefKind.Tuple when type.TupleKind is "Value" or "KVP":
                return "[" + string.Join(", ", type.Args.Select(a => Default(a, scope, name))) + "]";

            case TypeRefKind.Decl:
                switch (scope.Find(type.DeclId))
                {
                    case EnumDeclIr e:
                        return Is64(e.Underlying) ? "0n" : "0";
                    case ObjectDeclIr { IsStruct: true, IsAbstract: false } o:
                        return "new " + name(o) + "()";
                }

                return "null";

            default:
                return "null";
        }
    }

    /// <summary>True for the underlying types whose values are <c>bigint</c>.</summary>
    public static bool Is64(PrimKind prim) => prim is PrimKind.Int64 or PrimKind.UInt64;

    private static string PrimDefault(PrimKind prim) => prim switch
    {
        PrimKind.Bool => "false",
        PrimKind.Int64 or PrimKind.UInt64 or PrimKind.BigInteger => "0n",
        PrimKind.Char => "'\\0'",
        PrimKind.Decimal => "new " + Rt + "NexusDecimal(0n, 0, false)",
        PrimKind.DateTime => "new " + Rt + "NexusDateTime(0n)",
        PrimKind.DateTimeOffset => "new " + Rt + "NexusDateTimeOffset(0n, 0)",
        PrimKind.TimeSpan => "new " + Rt + "NexusTimeSpan(0n)",
        PrimKind.DateOnly => "new " + Rt + "NexusDateOnly(0)",
        PrimKind.TimeOnly => "new " + Rt + "NexusTimeOnly(0n)",
        PrimKind.Guid => "'00000000-0000-0000-0000-000000000000'",
        PrimKind.String or PrimKind.Uri or PrimKind.Version => "null",
        _ => "0",
    };

    /// <summary>
    /// The member's C# initializer as a JavaScript expression, or null when there is none or it has no exact
    /// JavaScript form (the member then starts at <see cref="Default"/>). Enum members are written <c>Enum.Member</c>.
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
                return prim is PrimKind.String or PrimKind.Uri ? JsSyntax.String(value) : null;

            case LiteralKind.Char:
                return value.Length == 1 && prim is PrimKind.Char or PrimKind.String ? JsSyntax.String(value) : null;

            case LiteralKind.Integer:
                return BigInteger.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var integer)
                    ? Integer(integer, prim)
                    : null;

            case LiteralKind.Real:
            case LiteralKind.Decimal:
                return Real(value, prim, literal.Kind == LiteralKind.Decimal);

            default:
                return null;
        }
    }

    private static string? Integer(BigInteger value, PrimKind prim)
    {
        bool InRange(BigInteger min, BigInteger max) => value >= min && value <= max;
        var text = value.ToString(CultureInfo.InvariantCulture);

        switch (prim)
        {
            case PrimKind.SByte:
                return InRange(sbyte.MinValue, sbyte.MaxValue) ? text : null;
            case PrimKind.Byte:
                return InRange(byte.MinValue, byte.MaxValue) ? text : null;
            case PrimKind.Int16:
                return InRange(short.MinValue, short.MaxValue) ? text : null;
            case PrimKind.UInt16:
                return InRange(ushort.MinValue, ushort.MaxValue) ? text : null;
            case PrimKind.Int32:
                return InRange(int.MinValue, int.MaxValue) ? text : null;
            case PrimKind.UInt32:
                return InRange(uint.MinValue, uint.MaxValue) ? text : null;
            case PrimKind.Int64:
                return InRange(long.MinValue, long.MaxValue) ? text + "n" : null;
            case PrimKind.UInt64:
                return InRange(ulong.MinValue, ulong.MaxValue) ? text + "n" : null;
            case PrimKind.BigInteger:
                return text + "n";
            case PrimKind.Single:
            case PrimKind.Half:
            case PrimKind.Double:
                // Exactly representable integers only (C# rounds the others at compile time).
                return BigInteger.Abs(value) <= (BigInteger.One << 24) ? text : null;
            case PrimKind.Char:
                return InRange(char.MinValue, char.MaxValue) ? JsSyntax.String(((char)(int)value).ToString()) : null;
            case PrimKind.Decimal:
                return Rt + "NexusDecimal.parse(" + JsSyntax.String(text) + ")";
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
                    ? Rt + "NexusDecimal.parse(" + JsSyntax.String(text) + ")"
                    : null;
            case PrimKind.Single:
                return isDecimal ? null : Floating(text, single: true);
            case PrimKind.Double:
                return isDecimal ? null : Floating(text, single: false);
            default:
                return null;
        }
    }

    private static string? Floating(string text, bool single)
    {
        switch (text)
        {
            case "NaN":
                return "NaN";
            case "Infinity":
            case "∞":
                return "Infinity";
            case "-Infinity":
            case "-∞":
                return "-Infinity";
        }

        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            return null;

        // The shortest round-trip form of the value C# stores: a float literal is rounded to float32.
        if (single)
        {
            var f = (float)number;
            return float.IsInfinity(f) ? null : Number(f);
        }

        return double.IsInfinity(number) ? null : Number(number);
    }

    /// <summary>A JavaScript number literal of the double (shortest round-trip form).</summary>
    public static string Number(double value)
    {
        if (double.IsNaN(value))
            return "NaN";
        if (double.IsInfinity(value))
            return value > 0 ? "Infinity" : "-Infinity";
        if (value == 0 && 1 / value < 0)
            return "-0";
        return value.ToString("R", CultureInfo.InvariantCulture);
    }
}
