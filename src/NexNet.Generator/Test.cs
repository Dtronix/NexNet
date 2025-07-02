using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

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
            context.RegisterSourceOutput(compilationAndSymbols, (spc, source) =>
            {
                var compilation = source.Left;
                var types = source.Right;

                foreach (var typeSymbol in types)
                {
                    var props = new List<string>();
                    var visited = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
                    WalkType(typeSymbol, visited, props);

                    var hash = ComputeHash(props);
                    var hintName = $"{typeSymbol.Name}_StructureHash.g.cs";
                    var generated = GenerateCode(typeSymbol, props, hash);

                    spc.AddSource(hintName, SourceText.From(generated, Encoding.UTF8));
                }
            });
        }

        private static void WalkType(
            ITypeSymbol rootType,
            HashSet<ITypeSymbol> visited,
            List<string> props)
        {
            var stack = new Stack<ITypeSymbol>();
            var attrsCache = new Dictionary<IPropertySymbol, int>(SymbolEqualityComparer.Default);
            stack.Push(rootType);

            while (stack.Count > 0)
            {
                attrsCache.Clear();
                var type = stack.Pop();
                if (type is null
                    || type.SpecialType != SpecialType.None
                    || !visited.Add(type))
                    continue;

                if (type is IArrayTypeSymbol arr)
                {
                    EnqueueType(arr.ElementType, stack, props, false, false);
                    continue;
                }

                if (type is INamedTypeSymbol named && named.Arity > 0)
                {
                    // Do a special promotion here to make the wrapped type null.
                    var isNullable = type.Name is "Nullable";
                    foreach (var ta in named.TypeArguments)
                        EnqueueType(ta, stack, props, true, isNullable);
                    continue;
                }

                var symbolName = type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
                if (type.ContainingNamespace?.Name.StartsWith("System", StringComparison.Ordinal) == true)
                    continue;

                // One pass over members, no LINQ:

                bool foundOrder = false;
                int counter = 0;
                foreach (var member in type.GetMembers())
                {
                    if (member is IPropertySymbol { DeclaredAccessibility: Accessibility.Public, IsStatic: false } prop)
                    {
                        var attr = prop.GetAttributes().FirstOrDefault(a => a.AttributeClass?.Name == "MemoryPackOrderAttribute");
                        
                        if (attr is { ConstructorArguments.Length: > 0 }
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

                    counter++;
                }

                // If any Order attrs, sort small list in place; else just iterate
                var propsToProcess = attrsCache.Keys.ToList();
                if (foundOrder)
                    propsToProcess.Sort((a, b) =>  attrsCache[a] - attrsCache[b]);

                foreach (var prop in propsToProcess)
                    EnqueueType(prop.Type, stack, props, true, false);
            }
        }

        private static void EnqueueType(ITypeSymbol type,
            Stack<ITypeSymbol> stack,
            List<string> props,
            bool addType,
            bool forceNullable)
        {
            if(addType && type.Name is not "Nullable")
                props.Add(GetSimpleType(type, forceNullable));
            
            stack.Push(type);
        }

        /*private static void WalkType(
            ITypeSymbol type,
            HashSet<ITypeSymbol> visited,
            List<string> props)
        {
            // Use SymbolEqualityComparer to ensure proper symbol comparison
            var ancestors = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
            WalkTypeInternal(type, visited, props, ancestors);
        }

        private static void WalkTypeInternal(
            ITypeSymbol type,
            HashSet<ITypeSymbol> visited,
            List<string> props,
            HashSet<ITypeSymbol> ancestors)
        {
            // If this is a special type, it can't change so walking is not needed.
            if (type.SpecialType != SpecialType.None)
            {
                //props.Add(type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat));
                return;
            }

            if (ancestors.Contains(type))
                return;

            visited.Add(type);
            ancestors.Add(type);

            if (type is INamedTypeSymbol named && named.Arity > 0)
            {
                foreach (var element in named.TypeArguments)
                {
                    props.Add(GetSimpleType(element));

                    WalkTypeInternal(element, visited, props, ancestors);
                }

                // Pop and return
                ancestors.Remove(type);
                return;
            }

            if (type is IArrayTypeSymbol array)
            {
                WalkTypeInternal(array.ElementType, visited, props, ancestors);

                // Pop and return
                ancestors.Remove(type);
                return;
            }

            if (type.ContainingNamespace != null)
            {
                if (type.ContainingNamespace.Name.StartsWith("System", StringComparison.Ordinal))
                {
                    ancestors.Remove(type);
                    return;
                }
            }

            var properties = type.GetMembers()
                .OfType<IPropertySymbol>()
                .Where(m => m.DeclaredAccessibility == Accessibility.Public
                            && !m.IsStatic
                            && !m.GetAttributes().Any(attr =>
                                attr.AttributeClass?.Name == "MemoryPackIgnoreAttribute"))
                .ToList();

            bool hasOrderAttr = properties.Any(prop =>
                prop.GetAttributes().Any(attr =>
                    attr.AttributeClass?.Name == "MemoryPackOrderAttribute"));

            var membersToProcess = hasOrderAttr
                ? properties.OrderBy(prop =>
                {
                    var attr = prop.GetAttributes()
                        .FirstOrDefault(a => a.AttributeClass?.Name == "MemoryPackOrderAttribute");
                    if (attr != null
                        && attr.ConstructorArguments.Length > 0
                        && attr.ConstructorArguments[0].Value is int order)
                    {
                        return order;
                    }

                    return int.MaxValue;
                })
                : properties.AsEnumerable();

            foreach (var prop in membersToProcess)
            {
                // Check if this property's type is marked [MemoryPackable]
                props.Add(GetSimpleType(prop.Type));
                WalkTypeInternal(prop.Type, visited, props, ancestors);
            }

            // Pop from the current path
            ancestors.Remove(type);
        }
        */

        public static string GetSimpleType(ITypeSymbol type, bool forceNullable)
        {
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
