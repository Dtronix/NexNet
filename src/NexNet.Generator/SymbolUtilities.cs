using System.Collections.Concurrent;
using System.Text;
using Microsoft.CodeAnalysis;

namespace NexNet.Generator;

internal class SymbolUtilities
{
    private static readonly ConcurrentBag<StringBuilder> _sbCache = new ConcurrentBag<StringBuilder>();

    public static StringBuilder GetStringBuilder()
    {
        if (!_sbCache.TryTake(out var sb))
            sb = new StringBuilder();

        return sb;
    }

    public static void ReturnStringBuilder(StringBuilder sb)
    {
        sb.Clear();
        _sbCache.Add(sb);
    }

    private static readonly SymbolDisplayFormat _symbolDisplayFormat = new SymbolDisplayFormat(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Included,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers
                              | SymbolDisplayMiscellaneousOptions.UseErrorTypeSymbolName);

    public static string GetFullSymbolType(ITypeSymbol? typeSymbol, bool extractTask)
    {
        if(typeSymbol == null)
            return "UNKNOWN TYPE";

        if (extractTask)
        {
            if (typeSymbol is INamedTypeSymbol namedTypeSymbol)
            {
                if (namedTypeSymbol.Arity == 1
                    && namedTypeSymbol.ConstructedFrom.MetadataName == "Task`1")
                {
                    typeSymbol = namedTypeSymbol.TypeArguments[0];
                }
                
            }
        }

        return FullSymbolType(typeSymbol);
    }
    
    public static string GetArityFullSymbolType(ITypeSymbol? typeSymbol, int index)
    {
        if(typeSymbol == null)
            return "UNKNOWN TYPE";

        if (typeSymbol is INamedTypeSymbol namedTypeSymbol)
        {
            typeSymbol = namedTypeSymbol.TypeArguments[index];
        }

        return FullSymbolType(typeSymbol);
    }
    
    public static string FullSymbolType(ITypeSymbol typeSymbol)
    {
        var sb = GetStringBuilder();
        sb.Append(typeSymbol.ToDisplayString(NullableFlowState.NotNull, _symbolDisplayFormat));

        if (typeSymbol.NullableAnnotation == NullableAnnotation.Annotated && sb[sb.Length - 1] != '?')
            sb.Append("?");

        if (sb.Length == 0)
            return "UNKNOWN TYPE " + typeSymbol.ToString();

        var result = sb.ToString();
        ReturnStringBuilder(sb);
        return result;
    }
}
