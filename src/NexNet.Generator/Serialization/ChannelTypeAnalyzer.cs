using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace NexNet.Generator.Serialization;

/// <summary>
/// Reports channel call sites (<c>CreateChannel&lt;T&gt;</c>, <c>GetChannel*&lt;T&gt;</c>) whose <c>T</c> will have no
/// registered formatter at runtime (NEXNET038).
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
internal sealed class ChannelTypeAnalyzer : DiagnosticAnalyzer
{
    private static readonly HashSet<string> ChannelMethods = new(StringComparer.Ordinal)
    {
        "CreateChannel", "GetChannel", "GetChannelReader", "GetChannelWriter"
    };

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(DiagnosticDescriptors.ChannelTypeNotSerializable);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            var declared = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
            var userFormatted = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
            foreach (var attr in start.Compilation.Assembly.GetAttributes())
            {
                if (attr.AttributeClass is not { IsGenericType: true } cls
                    || cls.ContainingNamespace?.ToDisplayString() != "NexNet.Serialization")
                    continue;

                if (cls.Name == "NexusSerializableAttribute")
                    declared.Add(cls.TypeArguments[0]);
                else if (cls.Name == "NexusFormatterAttribute" && cls.TypeArguments.Length == 2)
                    userFormatted.Add(cls.TypeArguments[1]);
            }

            start.RegisterOperationAction(ctx =>
            {
                var invocation = (IInvocationOperation)ctx.Operation;
                var method = invocation.TargetMethod;
                if (!ChannelMethods.Contains(method.Name) || method.TypeArguments.Length != 1)
                    return;

                var ns = method.ContainingType?.ContainingNamespace?.ToDisplayString();
                if (ns is not ("NexNet" or "NexNet.Pipes" or "NexNet.Invocation"))
                    return;

                var type = method.TypeArguments[0];
                if (type is ITypeParameterSymbol || IsRegistered(type, declared, userFormatted))
                    return;

                ctx.ReportDiagnostic(Diagnostic.Create(
                    DiagnosticDescriptors.ChannelTypeNotSerializable,
                    invocation.Syntax.GetLocation(),
                    type.ToDisplayString()));
            }, OperationKind.Invocation);
        });
    }

    /// <summary>
    /// True when a formatter for <paramref name="type"/> is registered at runtime without the type appearing in a
    /// nexus signature: built-ins, the pre-registered built-in closed generics, non-generic [NexusObject] types,
    /// user formatters and assembly-declared types.
    /// </summary>
    private static bool IsRegistered(ITypeSymbol type, HashSet<ITypeSymbol> declared, HashSet<ITypeSymbol> userFormatted)
    {
        type = type.WithNullableAnnotation(NullableAnnotation.NotAnnotated);
        if (declared.Contains(type) || userFormatted.Contains(type))
            return true;

        if (IsBuiltInLeaf(type))
            return true;

        if (type is INamedTypeSymbol { IsGenericType: false } named && SerializationBuilder.HasAttribute(named, "NexusObjectAttribute"))
            return true;

        // Pre-registered built-in closed generics (see BuiltInFormatters).
        if (type is IArrayTypeSymbol { Rank: 1 } array)
            return IsPrimitiveKind(array.ElementType) || array.ElementType.SpecialType == SpecialType.System_Byte || IsCommonElement(array.ElementType);

        if (type is INamedTypeSymbol { IsGenericType: true } generic)
        {
            var def = SerializationBuilder.MetadataFullName(generic.OriginalDefinition);
            var args = generic.TypeArguments;
            switch (def)
            {
                case "System.Nullable`1":
                    return IsBuiltInLeaf(args[0]);
                case "System.Collections.Generic.List`1":
                    return IsPrimitiveKind(args[0]) || args[0].SpecialType == SpecialType.System_Byte || IsCommonElement(args[0]);
                case "System.Memory`1":
                case "System.ReadOnlyMemory`1":
                    return IsPrimitiveKind(args[0]) || args[0].SpecialType == SpecialType.System_Byte;
                case "System.Collections.Generic.IList`1":
                case "System.Collections.Generic.IReadOnlyList`1":
                case "System.Collections.Generic.IEnumerable`1":
                case "System.Collections.Generic.ICollection`1":
                case "System.Collections.Generic.IReadOnlyCollection`1":
                    return IsCommonElement(args[0]);
            }
        }

        return false;
    }

    private static bool IsCommonElement(ITypeSymbol type)
        => type.SpecialType is SpecialType.System_Boolean or SpecialType.System_String or SpecialType.System_DateTime or SpecialType.System_Decimal
           || SerializationBuilder.MetadataFullName(type) == "System.Guid";

    private static bool IsBuiltInLeaf(ITypeSymbol type)
    {
        switch (type.SpecialType)
        {
            case SpecialType.System_Boolean:
            case SpecialType.System_Char:
            case SpecialType.System_SByte:
            case SpecialType.System_Byte:
            case SpecialType.System_Int16:
            case SpecialType.System_UInt16:
            case SpecialType.System_Int32:
            case SpecialType.System_UInt32:
            case SpecialType.System_Int64:
            case SpecialType.System_UInt64:
            case SpecialType.System_Single:
            case SpecialType.System_Double:
            case SpecialType.System_Decimal:
            case SpecialType.System_String:
            case SpecialType.System_DateTime:
                return true;
        }

        return SerializationBuilder.MetadataFullName(type) is "System.DateTimeOffset" or "System.TimeSpan" or "System.DateOnly"
            or "System.TimeOnly" or "System.Guid" or "System.Half" or "System.Numerics.BigInteger" or "System.Uri" or "System.Version";
    }

    private static bool IsPrimitiveKind(ITypeSymbol type)
        => type.SpecialType is SpecialType.System_SByte or SpecialType.System_Int16 or SpecialType.System_UInt16
               or SpecialType.System_Int32 or SpecialType.System_UInt32 or SpecialType.System_Int64 or SpecialType.System_UInt64
               or SpecialType.System_Single or SpecialType.System_Double or SpecialType.System_Char
           || SerializationBuilder.MetadataFullName(type) == "System.Half";
}
