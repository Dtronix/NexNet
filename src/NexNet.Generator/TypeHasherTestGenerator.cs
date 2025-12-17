using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace NexNet.Generator;

/// <summary>
/// Test generator for TypeHasher. Used to validate the hasher implementation.
/// Uses [GenerateStructureHashV2] attribute with expected WalkString value.
/// </summary>
[Generator(LanguageNames.CSharp)]
[SuppressMessage("MicrosoftCodeAnalysisReleaseTracking", "RS2008:Enable analyzer release tracking")]
[SuppressMessage("MicrosoftCodeAnalysisDesign", "RS1032:Define diagnostic message correctly")]
internal class TypeHasherTestGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // Register all class declarations with our custom attribute
        var classSymbols = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: (node, _) => node is ClassDeclarationSyntax { AttributeLists.Count: > 0 }
                    or InterfaceDeclarationSyntax { AttributeLists.Count: > 0 }
                    or StructDeclarationSyntax { AttributeLists.Count: > 0 },
                transform: (ctx, _) =>
                {
                    var model = ctx.SemanticModel;

                    ITypeSymbol? symbol = null;
                    if (ctx.Node is ClassDeclarationSyntax classDeclaration)
                        symbol = model.GetDeclaredSymbol(classDeclaration) as ITypeSymbol;
                    else if (ctx.Node is InterfaceDeclarationSyntax interfaceDeclaration)
                        symbol = model.GetDeclaredSymbol(interfaceDeclaration) as ITypeSymbol;
                    else if (ctx.Node is StructDeclarationSyntax structDeclaration)
                        symbol = model.GetDeclaredSymbol(structDeclaration) as ITypeSymbol;

                    if (symbol == null)
                        return null;

                    // Look for [GenerateStructureHashV2] attribute
                    if (symbol.GetAttributes()
                        .Any(ad => ad.AttributeClass?.Name == "GenerateStructureHashV2Attribute"))
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
            var hasher = new TypeHasher(generateWalkString: true);

            foreach (var typeSymbol in source.Right)
            {
                try
                {
                    var result = hasher.GetHashResult(typeSymbol);

                    // Get expected walk string from attribute
                    var attribute = typeSymbol.GetAttributes()
                        .First(a => a.AttributeClass?.Name == "GenerateStructureHashV2Attribute");

                    var expectedWalkArg = attribute.NamedArguments
                        .FirstOrDefault(a => a.Key == "ExpectedWalk");

                    if (expectedWalkArg.Key == null)
                    {
                        // No expected walk provided - report the calculated walk for reference
                        // Escape the walk string for display
                        var escapedWalk = EscapeForDiagnostic(result.WalkString ?? "");
                        context.ReportDiagnostic(Diagnostic.Create(
                            _testInfo001,
                            null,
                            typeSymbol.Name,
                            result.Hash,
                            escapedWalk));
                        continue;
                    }

                    var expectedWalk = expectedWalkArg.Value.Value?.ToString() ?? "";
                    var actualWalk = result.WalkString ?? "";

                    // Normalize line endings for comparison
                    expectedWalk = NormalizeLineEndings(expectedWalk);
                    actualWalk = NormalizeLineEndings(actualWalk);

                    if (expectedWalk != actualWalk)
                    {
                        context.ReportDiagnostic(Diagnostic.Create(
                            _testFail001,
                            null,
                            typeSymbol.Name,
                            EscapeForDiagnostic(actualWalk),
                            EscapeForDiagnostic(expectedWalk)));
                    }
                }
                catch (Exception e)
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        _testFail999,
                        null,
                        typeSymbol.Name,
                        e.ToString()));
                }
            }
        });
    }

    private static string NormalizeLineEndings(string s)
    {
        return s.Replace("\r\n", "\n").Replace("\r", "\n").TrimEnd('\n');
    }

    private static string EscapeForDiagnostic(string s)
    {
        return s.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
    }

    private static readonly DiagnosticDescriptor _testInfo001 = new DiagnosticDescriptor(
        id: "TESTV2_INFO001",
        title: "TypeHasher Result",
        messageFormat: "Type '{0}' hash={1} walk='{2}'",
        category: "GENERATOR_TESTS_V2",
        defaultSeverity: DiagnosticSeverity.Info,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor _testFail001 = new DiagnosticDescriptor(
        id: "TESTV2_FAIL001",
        title: "TypeHasher Walk Mismatch",
        messageFormat: "Type '{0}' walk mismatch.\nActual:   '{1}'\nExpected: '{2}'",
        category: "GENERATOR_TESTS_V2",
        defaultSeverity: DiagnosticSeverity.Info,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor _testFail999 = new DiagnosticDescriptor(
        id: "TESTV2_FAIL999",
        title: "TypeHasher Exception",
        messageFormat: "Exception processing type '{0}': {1}",
        category: "GENERATOR_TESTS_V2",
        defaultSeverity: DiagnosticSeverity.Info,
        isEnabledByDefault: true);
}
