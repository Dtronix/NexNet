using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace NexNet.Generator.Targets.CSharpTest;

/// <summary>C# spelling helpers of the test-scaffolding emitters.</summary>
internal static class CSharpNames
{
    /// <summary>Fully qualified type names with nullable reference annotations.</summary>
    public static readonly SymbolDisplayFormat TypeFormat = SymbolDisplayFormat.FullyQualifiedFormat
        .AddMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    /// <summary>The identifier, prefixed with '@' when it is a C# keyword.</summary>
    public static string Escape(string name)
        => SyntaxFacts.GetKeywordKind(name) != SyntaxKind.None ? "@" + name : name;

    /// <summary>A C# string literal.</summary>
    public static string Literal(string value) => SymbolDisplay.FormatLiteral(value, quote: true);

    /// <summary>The type of a symbol, fully qualified.</summary>
    public static string Type(ITypeSymbol type) => type.ToDisplayString(TypeFormat);

    /// <summary>A hint-name-safe form of a metadata name.</summary>
    public static string HintSafe(string name)
    {
        var chars = name.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (!(char.IsLetterOrDigit(chars[i]) || chars[i] is '_' or '.'))
                chars[i] = '_';
        }

        return new string(chars);
    }

    /// <summary>The C# default value of a parameter with an explicit default, or null.</summary>
    public static string? DefaultValue(IParameterSymbol parameter)
    {
        if (!parameter.HasExplicitDefaultValue)
            return null;

        var value = parameter.ExplicitDefaultValue;
        if (value is null)
            return "default";

        var type = Type(parameter.Type);
        return value switch
        {
            string s => Literal(s),
            char c => SymbolDisplay.FormatLiteral(c, quote: true),
            bool b => b ? "true" : "false",
            _ => "(" + type + ")(" + SymbolDisplay.FormatPrimitive(value, quoteStrings: true, useHexadecimalNumbers: false) + ")",
        };
    }

    /// <summary>The parameter modifier keyword with a trailing space (ref/out/in), or empty.</summary>
    public static string RefPrefix(IParameterSymbol parameter) => parameter.RefKind switch
    {
        RefKind.Ref => "ref ",
        RefKind.Out => "out ",
        RefKind.In => "in ",
        RefKind.RefReadOnlyParameter => "ref readonly ",
        _ => "",
    };

    public static string Int(int value) => value.ToString(CultureInfo.InvariantCulture);
}
