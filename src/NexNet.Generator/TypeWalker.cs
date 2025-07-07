using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace PropertyStructureGenerators
{
    [Generator(LanguageNames.CSharp)]
    public class PropertyStructureHashGenerator : IIncrementalGenerator
    {
        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            // Register all class declarations with our custom attribute
            var classSymbols = context.SyntaxProvider
                .CreateSyntaxProvider(
                    predicate: (node, _) => node is ClassDeclarationSyntax cds && cds.AttributeLists.Count > 0,
                    transform: (ctx, _) =>
                    {
                        var classDecl = (ClassDeclarationSyntax)ctx.Node;
                        var model = ctx.SemanticModel;
                        var symbol = model.GetDeclaredSymbol(classDecl) as ITypeSymbol;
                        if (symbol == null)
                            return null;

                        // Look for [GenerateStructureHash] attribute
                        if (symbol.GetAttributes().Any(ad => ad.AttributeClass?.Name == "GenerateStructureHashAttribute"))
                            return symbol;
                        return null;
                    })
                .Where(symbol => symbol != null)
                .Select((s, _) => s!)
                .Collect();

            // Combine with compilation
            var compilationAndSymbols = context.CompilationProvider.Combine(classSymbols);

            // Register source output
            context.RegisterSourceOutput(compilationAndSymbols, (context, source) =>
            {
                var compilation = source.Left;
                var types = source.Right;
                
                context.ReportDiagnostic(Diagnostic.Create(new DiagnosticDescriptor(id: "",
                        title: "Nexus must not be generic",
                        messageFormat: "The Nexus '{0}' must not be generic",
                        category: ,
                        defaultSeverity: DiagnosticSeverity.Error,
                        isEnabledByDefault: true);
                    )));

                foreach (var typeSymbol in types)
                {
                    
                    var props = WalkType(typeSymbol);
                    // Compare the hash and properties
                    var attribute = typeSymbol.GetAttributes()
                        .First(a => a.AttributeClass?.Name == "GenerateStructureHashAttribute");
                    
                    var attProps = attribute.NamedArguments.First(a => a.Key == "Properties");
                    var values = attProps.Value.Values;
                    for (int i = 0; i < values.Length; i++)
                    {
                        var stringValue = values[i].Value!.ToString();
                        if(stringValue != props[i])
                            throw new Exception($"Property index {i} {props[i]} doesn't match required property value of {stringValue}");
                    }
                }
            });
        }
        

        public static List<string> WalkType(ITypeSymbol rootType)
        {
            var props = new List<string>();
            // stack holds (node, setOfAncestors)
            var stack = new Stack<(ITypeSymbol Type, HashSet<ITypeSymbol> Ancestors)>();
            stack.Push((rootType, new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default)));

            var attrsCache = new Dictionary<ISymbol, int>(SymbolEqualityComparer.Default);

            while (stack.Count > 0)
            {
                attrsCache.Clear();
                var (type, ancestors) = stack.Pop();

                // skip nulls or true cycles only
                if (type is null || ancestors.Contains(type))
                    continue;
                
                var newAncestors = new HashSet<ITypeSymbol>(ancestors, SymbolEqualityComparer.Default) { type };
                
                if (type.SpecialType != SpecialType.None)
                {
                    props.Add(GetSimpleType(type, false, false, false));
                    continue;
                }
                
                if (type is IArrayTypeSymbol arr)
                {
                    EnqueueType(arr.ElementType, stack, props, newAncestors, false, false);
                    continue;
                }
                
                if (type is INamedTypeSymbol named && named.Arity > 0)
                {
                    var isNullable = type.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T;
                    foreach (var ta in named.TypeArguments)
                    {
                        EnqueueType(ta, stack, props, newAncestors, true, isNullable);
                    }

                    continue;
                }
                
                if (type.ContainingNamespace?.Name.StartsWith("System", StringComparison.Ordinal) == true)
                    continue;

                // collect [MemoryPackOrder] or declaration order
                bool foundOrder = false;
                int counter = 0;
                foreach (var member in type.GetMembers())
                {
                    if (member is IPropertySymbol { DeclaredAccessibility: Accessibility.Public, IsStatic: false } prop)
                    {
                        var attr = prop.GetAttributes()
                            .FirstOrDefault(a => a.AttributeClass?.Name == "MemoryPackOrderAttribute");
                        if (attr?.ConstructorArguments.Length > 0
                            && attr.ConstructorArguments[0].Value is int order)
                        {
                            attrsCache[prop] = order;
                            foundOrder = true;
                        }
                        else
                        {
                            attrsCache[prop] = counter;
                        }
                    }
                    else if (member is IFieldSymbol { DeclaredAccessibility: Accessibility.Public, IsStatic: false } field)
                    {
                        var attr = field.GetAttributes()
                            .FirstOrDefault(a => a.AttributeClass?.Name == "MemoryPackOrderAttribute");
                        if (attr?.ConstructorArguments.Length > 0
                            && attr.ConstructorArguments[0].Value is int order)
                        {
                            attrsCache[field] = order;
                            foundOrder = true;
                        }
                        else
                        {
                            attrsCache[field] = counter;
                        }
                    }

                    counter++;
                }

                // sort if needed
                var membersToProcess = attrsCache.Keys.ToList();
                if (foundOrder)
                    membersToProcess.Sort((a, b) => attrsCache[a] - attrsCache[b]);

                // enqueue each property’s type
                foreach (var member in membersToProcess)
                {
                    ITypeSymbol memberType;
                    if(member is IPropertySymbol propSymbol)
                        type = propSymbol.Type;
                    else if(member is IFieldSymbol fieldSymbol)
                        type = fieldSymbol.Type;
                    
                    EnqueueType(type, stack, props, newAncestors, true, false);
                }
            }

            return props;
            
            static void EnqueueType(
                ITypeSymbol type,
                Stack<(ITypeSymbol Type, HashSet<ITypeSymbol> Ancestors)> stack,
                List<string> props,
                HashSet<ITypeSymbol> ancestors,
                bool addType,
                bool forceNullable)
            {
                var forceArray = false;
                if (type is IArrayTypeSymbol { ElementType.OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } array 
                    && array.ElementType is INamedTypeSymbol namedType)
                {
                    type = namedType.TypeArguments[0];
                    props.Add(GetSimpleType(
                        type, 
                        array.ElementNullableAnnotation == NullableAnnotation.Annotated,
                        true,
                        array.NullableAnnotation == NullableAnnotation.Annotated));
                }
                else if (addType && type.OriginalDefinition.SpecialType != SpecialType.System_Nullable_T)
                {
                    props.Add(GetSimpleType(type, forceNullable, forceArray, false));
                }
                else if(IsNullable(type, out var nullableType))
                {
                    type = nullableType!;
                    props.Add(GetSimpleType(type, true, false, false));
                }
                    
                    
                if (type.SpecialType == SpecialType.None)
                    stack.Push((type, ancestors));
            }

            static bool IsNullable(ITypeSymbol type, out ITypeSymbol? nullableType)
            {
                if (type.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T
                    && type is INamedTypeSymbol namedType
                    && namedType.Arity == 1)
                {
                    nullableType = namedType.TypeArguments[0];
                    return true;
                }

                nullableType = null;
                return false;
            }
            
            static string GetSimpleType(ITypeSymbol type, bool forceNullable, bool forceArray, bool forceArrayNullable)
            {
                if (forceArray && forceNullable)
                    return forceArrayNullable ? $"{type.Name}?[]?" : $"{type.Name}?[]";

                if (type is IArrayTypeSymbol arrayType)
                {
                    if(arrayType.NullableAnnotation == NullableAnnotation.Annotated || forceNullable)
                        return arrayType.ElementNullableAnnotation == NullableAnnotation.Annotated
                            ? $"{arrayType.ElementType.Name}?[]?"
                            : $"{arrayType.ElementType.Name}[]?";

                    return arrayType.ElementNullableAnnotation == NullableAnnotation.Annotated || forceNullable
                        ? $"{arrayType.ElementType.Name}?[]"
                        : $"{arrayType.ElementType.Name}[]";
                }

                return type.NullableAnnotation == NullableAnnotation.Annotated || forceNullable
                    ? $"{type.Name}?"
                    : $"{type.Name}";
            }
        }

        private static string ComputeHash(List<string> props)
        {
            using var sha = SHA256.Create();
            var input = string.Join(";", props);
            var bytes = Encoding.UTF8.GetBytes(input);
            var hashBytes = sha.ComputeHash(bytes);
            return BitConverter.ToString(hashBytes).Replace("-", string.Empty).ToLowerInvariant();
        }

        private static string GenerateCode(ITypeSymbol typeSymbol, List<string> props, string hash)
        {
            var ns = typeSymbol.ContainingNamespace.IsGlobalNamespace
                ? string.Empty
                : $"namespace {typeSymbol.ContainingNamespace.ToDisplayString()}\n{{\n";
            var closeNs = typeSymbol.ContainingNamespace.IsGlobalNamespace ? string.Empty : "}\n";
            var className = typeSymbol.Name + "StructureHash";

            // Generate property array
            var sbProps = new StringBuilder();
            for (int i = 0; i < props.Count; i++)
            {
                sbProps.Append("\"" + props[i] + "\"");
                if (i < props.Count - 1)
                    sbProps.Append(", ");
            }

            return $"// <auto-generated/>\nusing System.Collections.Generic;\n{ns}    public static partial class {className}\n    {{\n        public static IReadOnlyList<string> PropertyPaths {{ get; }} = new List<string> {{ {sbProps} }};\n        public static string Hash {{ get; }} = \"{hash}\";\n    }}\n{closeNs}";
        }
    }
}
