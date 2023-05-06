using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace NexNet.Generator;

// dotnet/runtime generators.

// https://github.com/dotnet/runtime/blob/main/src/libraries/System.Text.RegularExpressions/gen/
// https://github.com/dotnet/runtime/tree/main/src/libraries/System.Text.Json/gen
// https://github.com/dotnet/runtime/tree/main/src/libraries/System.Private.CoreLib/gen
// https://github.com/dotnet/runtime/tree/main/src/libraries/Microsoft.Extensions.Logging.Abstractions/gen
// https://github.com/dotnet/runtime/tree/main/src/libraries/System.Runtime.InteropServices.JavaScript/gen/JSImportGenerator
// https://github.com/dotnet/runtime/tree/main/src/libraries/System.Runtime.InteropServices/gen/LibraryImportGenerator
// https://github.com/dotnet/runtime/tree/main/src/tests/Common/XUnitWrapperGenerator

// documents, blogs.

// https://github.com/dotnet/roslyn/blob/main/docs/features/incremental-generators.md
// https://andrewlock.net/creating-a-source-generator-part-1-creating-an-incremental-source-generator/
// https://qiita.com/WiZLite/items/48f37278cf13be899e40
// https://zenn.dev/pcysl5edgo/articles/6d9be0dd99c008
// https://neue.cc/2021/05/08_600.html
// https://www.thinktecture.com/en/net/roslyn-source-generators-introduction/

// for check generated file
// <EmitCompilerGeneratedFiles>true</EmitCompilerGeneratedFiles>
// <CompilerGeneratedFilesOutputPath>Generated</CompilerGeneratedFilesOutputPath>

[Generator(LanguageNames.CSharp)]
internal partial class NexNetHubGenerator : IIncrementalGenerator
{
    public const string NexNetHubAttributeFullName = "NexNet.NexNetHubAttribute`2";

    public NexNetHubGenerator()
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
                NexNetHubAttributeFullName,
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

    
    class GeneratorContext : IGeneratorContext
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
