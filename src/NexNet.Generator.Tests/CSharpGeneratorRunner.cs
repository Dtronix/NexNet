﻿using System.Runtime.CompilerServices;
using MemoryPack;
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
            .Append(typeof(MemoryPackableAttribute).Assembly.Location) // System Assemblies 
            .Append(typeof(System.IO.Pipelines.IDuplexPipe).Assembly.Location) // System Assemblies 
            .Select(x => MetadataReference.CreateFromFile(x))
            .ToArray();

        var compilation = CSharpCompilation.Create("generatortest",
            references: references,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        baseCompilation = compilation;
    }

    public static Diagnostic[] RunGenerator(string source, string[]? preprocessorSymbols = null, AnalyzerConfigOptionsProvider? options = null)
    {
        if (preprocessorSymbols == null)
        {
            preprocessorSymbols = new[] { "NET7_0_OR_GREATER" };
        }
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
        return diagnostics.Concat(compilationDiagnostics).Where(x => x.Severity == DiagnosticSeverity.Error).ToArray();
    }
}
