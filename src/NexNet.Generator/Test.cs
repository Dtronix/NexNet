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
                    WalkType(typeSymbol, visited, props, string.Empty);

                    var hash = ComputeHash(props);
                    var hintName = $"{typeSymbol.Name}_StructureHash.g.cs";
                    var generated = GenerateCode(typeSymbol, props, hash);

                    spc.AddSource(hintName, SourceText.From(generated, Encoding.UTF8));
                }
            });
        }

        private static void WalkType(
            ITypeSymbol type,
            HashSet<ITypeSymbol> visited,
            List<string> props,
            string prefix)
        {
            if (type == null || visited.Contains(type))
                return;
            visited.Add(type);

            // Handle tuple types
            if (type is INamedTypeSymbol named && named.IsTupleType)
            {
                foreach (var element in named.TupleElements)
                {
                    var name = element.Name;
                    var elementType = element.Type;
                    var path = string.IsNullOrEmpty(prefix) ? name : prefix + "." + name;
                    props.Add(path);
                    WalkType(elementType, visited, props, path);
                }
                return;
            }

            // Handle List<T>
            if (type is INamedTypeSymbol listType &&
                listType.OriginalDefinition.ToDisplayString() == "System.Collections.Generic.List<T>")
            {
                var itemType = listType.TypeArguments[0];
                // Use [] to denote collection
                var path = (string.IsNullOrEmpty(prefix) ? string.Empty : prefix) + "[]";
                WalkType(itemType, visited, props, path);
                return;
            }

            // Walk public instance properties
            foreach (var member in type.GetMembers().OfType<IPropertySymbol>())
            {
                if (member.DeclaredAccessibility != Accessibility.Public || member.IsStatic)
                    continue;

                var name = member.Name;
                var path = string.IsNullOrEmpty(prefix) ? name : prefix + "." + name;
                props.Add(path);
                WalkType(member.Type, visited, props, path);
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
