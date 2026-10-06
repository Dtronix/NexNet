using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace NexNet.Generator.Tests;

public static class CSharpGeneratorRunner
{
    static Compilation baseCompilation = default!;

    [ModuleInitializer]
    public static void InitializeCompilation()
    {
        // running .NET Core system assemblies dir path
        var baseAssemblyPath = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        var systemAssemblies = Directory.GetFiles(baseAssemblyPath)
            .Where(x =>
            {
                var fileName = Path.GetFileName(x);
                if (fileName.EndsWith("Native.dll")) return false;
                return fileName.StartsWith("System") || fileName is "mscorlib.dll" or "netstandard.dll";
            });

        var references = systemAssemblies
            .Append(typeof(NexusAttribute<,>).Assembly.Location) // System Assemblies 
            .Append(typeof(System.IO.Pipelines.IDuplexPipe).Assembly.Location) // System Assemblies 
            .Select(x => MetadataReference.CreateFromFile(x))
            .ToArray();

        var compilation = CSharpCompilation.Create("generatortest",
            references: references,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        baseCompilation = compilation;
    }

    /// <summary>
    /// Creates a compilation of <paramref name="source"/> with the standard test references (no generators run).
    /// </summary>
    public static Compilation CreateCompilation(string source)
    {
        var parseOptions = new CSharpParseOptions(LanguageVersion.CSharp13);
        return baseCompilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(source, parseOptions));
    }

    public static Diagnostic[] RunGenerator(
        string source, 
        string[]? preprocessorSymbols = null,
        DiagnosticSeverity minDiagnostic = DiagnosticSeverity.Error, 
        AnalyzerConfigOptionsProvider? options = null)
    {
        var parseOptions = new CSharpParseOptions(LanguageVersion.CSharp11, preprocessorSymbols: preprocessorSymbols);
        
        var driver = CSharpGeneratorDriver.Create(new NexusGenerator()).WithUpdatedParseOptions(parseOptions);
        if (options != null)
        {
            driver = (CSharpGeneratorDriver)driver.WithUpdatedAnalyzerConfigOptions(options);
        }

        var compilation = baseCompilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(source, parseOptions));

        driver.RunGeneratorsAndUpdateCompilation(compilation, out var newCompilation, out var diagnostics);

        // combine diagnostics as result.(ignore warning)
        var compilationDiagnostics = newCompilation.GetDiagnostics();
        return diagnostics.Concat(compilationDiagnostics).Where(x => x.Severity >= minDiagnostic).ToArray();
    }

    /// <summary>
    /// Runs the NexNet generator with incremental step tracking and returns the driver (for re-runs), the run
    /// result (generated sources and tracked steps) and the error diagnostics of the updated compilation.
    /// </summary>
    public static (GeneratorDriver Driver, GeneratorDriverRunResult Result, Diagnostic[] Errors) RunGeneratorWithResult(
        Compilation compilation,
        GeneratorDriver? driver = null)
    {
        driver ??= CSharpGeneratorDriver.Create(
            new[] { new NexusGenerator().AsSourceGenerator() },
            parseOptions: new CSharpParseOptions(LanguageVersion.CSharp13),
            driverOptions: new GeneratorDriverOptions(IncrementalGeneratorOutputKind.None, trackIncrementalGeneratorSteps: true));

        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var newCompilation, out var diagnostics);
        var errors = diagnostics.Concat(newCompilation.GetDiagnostics())
            .Where(x => x.Severity >= DiagnosticSeverity.Error).ToArray();
        return (driver, driver.GetRunResult(), errors);
    }
}
