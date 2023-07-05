using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace NexNet.Generator;

[Generator(LanguageNames.CSharp)]
internal partial class NexusGenerator : IIncrementalGenerator
{
    public const string NexusAttributeFullName = "NexNet.NexusAttribute`2";

    public NexusGenerator()
    {
        
    }

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // no need RegisterPostInitializationOutput

        Register(context);
    }

    void Register(IncrementalGeneratorInitializationContext context)
    {
        // return dir of info output or null .
        var typeDeclarations = context.SyntaxProvider.ForAttributeWithMetadataName(
                NexusAttributeFullName,
                predicate: static (node, token) =>
                {
                    // search [NexNetHubAttribute] class or struct or interface or record
                    return (node is ClassDeclarationSyntax);
                },
                transform: static (context, token) =>
                {
                    return (TypeDeclarationSyntax)context.TargetNode;
                });

        var parseOptions = context.ParseOptionsProvider.Select((parseOptions, token) =>
        {
            var csOptions = (CSharpParseOptions)parseOptions;
            var langVersion = csOptions.LanguageVersion;
            return langVersion;
        });

        {
            var source = typeDeclarations
                .Combine(context.CompilationProvider)
                .WithComparer(Comparer.Instance)
                .Combine(parseOptions);

            context.RegisterSourceOutput(source, static (context, source) =>
            {
                var (typeDeclaration, compilation) = source.Left;
                var logPath = source.Left.Item2;
                var langVersion = source.Right;

                Generate(typeDeclaration, compilation, new GeneratorContext(context, langVersion));
            });
        }
    }

    class Comparer : IEqualityComparer<(TypeDeclarationSyntax, Compilation)>
    {
        public static readonly Comparer Instance = new();

        public bool Equals((TypeDeclarationSyntax, Compilation) x, (TypeDeclarationSyntax, Compilation) y)
        {
            return x.Item1.Equals(y.Item1);
        }

        public int GetHashCode((TypeDeclarationSyntax, Compilation) obj)
        {
            return obj.Item1.GetHashCode();
        }
    }

    
    internal class GeneratorContext
    {
        readonly SourceProductionContext _context;

        public GeneratorContext(SourceProductionContext context, LanguageVersion languageVersion)
        {
            this._context = context;
            this.LanguageVersion = languageVersion;
        }

        public CancellationToken CancellationToken => _context.CancellationToken;

        public Microsoft.CodeAnalysis.CSharp.LanguageVersion LanguageVersion { get; }

        public void AddSource(string hintName, string source)
        {
            _context.AddSource(hintName, source);
        }

        public void ReportDiagnostic(Diagnostic diagnostic)
        {
            _context.ReportDiagnostic(diagnostic);
        }
    }
}
