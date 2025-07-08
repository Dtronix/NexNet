using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace NexNet.Generator;

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
                    or InterfaceDeclarationSyntax { AttributeLists.Count: > 0 },
                transform: (ctx, _) =>
                {
   
                    var model = ctx.SemanticModel;

                    ITypeSymbol? symbol = null;
                    if(ctx.Node is ClassDeclarationSyntax classDeclaration)
                        symbol = model.GetDeclaredSymbol(classDeclaration) as ITypeSymbol;
                    else if(ctx.Node is InterfaceDeclarationSyntax interfaceDeclaration)
                        symbol = model.GetDeclaredSymbol(interfaceDeclaration) as ITypeSymbol;
                    
                    if (symbol == null)
                        return null;

                    // Look for [GenerateStructureHash] attribute
                    if (symbol.GetAttributes()
                        .Any(ad => ad.AttributeClass?.Name == "GenerateStructureHashAttribute"))
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
            //var compilation = source.Left;

            foreach (var typeSymbol in source.Right)
            {
                try
                {
                    var props = TypeHasher.Walk(typeSymbol, false);
                    // Compare the hash and properties
                    var attribute = typeSymbol.GetAttributes()
                        .First(a => a.AttributeClass?.Name == "GenerateStructureHashAttribute");

                    var values = attribute.NamedArguments.First(a => a.Key == "Properties").Value.Values
                        .Select(v => v.Value!.ToString()).ToArray();
                    var hashInt =
                        int.Parse(attribute.NamedArguments.First(a => a.Key == "Hash").Value.Value!.ToString(),
                            NumberStyles.Integer);

                    if (values.Length != props.Count)
                    {
                        ReportDiagnostic(context,
                            Diagnostic.Create(_testFail002, null, props.Count, values.Length),
                            props, values);
                    }

                    var stringHash = new XxHash32();
                    var hash = new HashCode();
                    for (int i = 0; i < values.Length; i++)
                    {
                        if (values[i] != props[i])
                        {
                            ReportDiagnostic(context,
                                Diagnostic.Create(_testFail001, null, i, props[i], values[i]),
                                props, values);
                        }

                        hash.Add((int)stringHash.ComputeHash(Encoding.UTF8.GetBytes(props[i])));
                    }

                    var calculatedHash = hash.ToHashCode();
                    if (hashInt != calculatedHash)
                    {
                        ReportDiagnostic(context,
                            Diagnostic.Create(_testFail003, null, calculatedHash, hashInt),
                            props, values);
                    }
                }
                catch (Exception e)
                {
                    ReportDiagnostic(context,
                        Diagnostic.Create(_testFail998, null, e.ToString()),
                        null, null);
                }

            }
        });
    }

    private bool _reportedStructure = false;

    private void ReportDiagnostic(SourceProductionContext context,
        Diagnostic? diagnostic,
        List<string>? parsed,
        string[]? required)
    {
        if (diagnostic != null)
            context.ReportDiagnostic(diagnostic);

        if (parsed != null && required != null && !_reportedStructure)
        {
            _reportedStructure = true;
            context.ReportDiagnostic(Diagnostic.Create(
                _testFail999,
                null,
                $"[\"{string.Join("\", \"", parsed)}\"]",
                $"[\"{string.Join("\", \"", required)}\"]"));
        }
    }

    private static DiagnosticDescriptor _testFail001 = new DiagnosticDescriptor(
        id: "TEST_FAIL001",
        title: "",
        messageFormat: "Property index {0} {1} doesn't match required property value of {2}",
        category: "GENERATOR_TESTS",
        defaultSeverity: DiagnosticSeverity.Info,
        isEnabledByDefault: true);

    private static DiagnosticDescriptor _testFail002 = new DiagnosticDescriptor(
        id: "TEST_FAIL002",
        title: "",
        messageFormat: "Parsed members contained {0} members while the required number of members is {1}",
        category: "GENERATOR_TESTS",
        defaultSeverity: DiagnosticSeverity.Info,
        isEnabledByDefault: true);

    private static DiagnosticDescriptor _testFail003 = new DiagnosticDescriptor(
        id: "TEST_FAIL003",
        title: "",
        messageFormat: "Parsed members hash {0} did not match required hash of {1}",
        category: "GENERATOR_TESTS",
        defaultSeverity: DiagnosticSeverity.Info,
        isEnabledByDefault: true);

    private static DiagnosticDescriptor _testFail998 = new DiagnosticDescriptor(
        id: "TEST_FAIL998",
        title: "",
        messageFormat: "Exception occurred {0}",
        category: "GENERATOR_TESTS",
        defaultSeverity: DiagnosticSeverity.Info,
        isEnabledByDefault: true);

    private static DiagnosticDescriptor _testFail999 = new DiagnosticDescriptor(
        id: "TEST_FAIL999",
        title: "",
        messageFormat: "Parsed members did not match required members.\nParsed   {0}\nRequired {1}",
        category: "GENERATOR_TESTS",
        defaultSeverity: DiagnosticSeverity.Info,
        isEnabledByDefault: true);

}
