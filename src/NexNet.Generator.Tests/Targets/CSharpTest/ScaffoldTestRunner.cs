using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NexNet.Generator.Targets.CSharpTest;
using NexNet.Interop;

namespace NexNet.Generator.Tests.Targets.CSharpTest;

/// <summary>One generator run over scaffolding sources.</summary>
internal sealed record ScaffoldRun(
    Compilation Output,
    Dictionary<string, string> Scaffolding,
    Diagnostic[] GeneratorDiagnostics,
    Diagnostic[] Errors,
    Diagnostic[] GeneratedWarnings)
{
    public string[] Messages(string id) => GeneratorDiagnostics
        .Where(d => d.Id == id)
        .Select(d => d.GetMessage(System.Globalization.CultureInfo.InvariantCulture))
        .ToArray();

    /// <summary>The generated file of a class, e.g. <c>Ns.Remote_IServer</c>.</summary>
    public string File(string qualifiedClass)
        => Scaffolding.TryGetValue(TestScaffoldingExtractor.HintPrefix + qualifiedClass + ".g.cs", out var code)
            ? code
            : throw new InvalidOperationException("No scaffolding for " + qualifiedClass + ". Generated: " + string.Join(", ", Scaffolding.Keys));

    /// <summary>Emits the compilation and loads it (a fresh assembly name each time).</summary>
    public System.Reflection.Assembly Load()
    {
        using var stream = new MemoryStream();
        var result = Output.WithAssemblyName("scaffold_" + Guid.NewGuid().ToString("N")).Emit(stream);
        if (!result.Success)
            throw new InvalidOperationException("Emit failed: " + string.Join("\n", result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        return System.Reflection.Assembly.Load(stream.ToArray());
    }
}

/// <summary>Runs the real generator over sources with nullable enabled and the Abstractions referenced.</summary>
internal static class ScaffoldTestRunner
{
    public const string Usings = """
        #nullable enable
        using System;
        using System.Collections.Generic;
        using System.Threading;
        using System.Threading.Tasks;
        using NexNet;
        using NexNet.Collections;
        using NexNet.Collections.Lists;
        using NexNet.Interop;
        using NexNet.Pipes;
        using NexNet.Serialization;

        """;

    private static readonly MetadataReference[] References = CreateReferences();

    private static MetadataReference[] CreateReferences()
    {
        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        return Directory.GetFiles(runtimeDir)
            .Where(f =>
            {
                var name = Path.GetFileName(f);
                return !name.EndsWith("Native.dll", StringComparison.Ordinal)
                       && (name.StartsWith("System", StringComparison.Ordinal) || name is "mscorlib.dll" or "netstandard.dll");
            })
            .Append(typeof(NexusAttribute<,>).Assembly.Location)
            .Append(typeof(System.IO.Pipelines.IDuplexPipe).Assembly.Location)
            .Append(typeof(IRemoteInvoker).Assembly.Location)
            .Distinct()
            .Select(f => (MetadataReference)MetadataReference.CreateFromFile(f))
            .ToArray();
    }

    public static ScaffoldRun Run(params string[] sources)
    {
        var parseOptions = new CSharpParseOptions(LanguageVersion.CSharp13);
        var compilation = CSharpCompilation.Create(
            "scaffoldtest",
            sources.Select((s, i) => CSharpSyntaxTree.ParseText(s, parseOptions, path: $"Source{i}.cs")),
            References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            new[] { new NexusGenerator().AsSourceGenerator() },
            parseOptions: parseOptions,
            driverOptions: new GeneratorDriverOptions(IncrementalGeneratorOutputKind.None, trackIncrementalGeneratorSteps: true));
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out var generatorDiagnostics);

        var result = driver.GetRunResult();
        if (result.Results.FirstOrDefault(r => r.Exception != null).Exception is { } exception)
            throw new InvalidOperationException("The generator threw.", exception);

        var scaffolding = result.Results
            .SelectMany(r => r.GeneratedSources)
            .Where(s => s.HintName.StartsWith(TestScaffoldingExtractor.HintPrefix, StringComparison.Ordinal))
            .ToDictionary(s => s.HintName, s => s.SourceText.ToString());

        var all = output.GetDiagnostics();
        return new ScaffoldRun(
            output,
            scaffolding,
            generatorDiagnostics.ToArray(),
            all.Where(d => d.Severity == DiagnosticSeverity.Error).ToArray(),
            all.Where(d => d.Severity == DiagnosticSeverity.Warning
                           && d.Location.SourceTree?.FilePath.Contains(TestScaffoldingExtractor.HintPrefix, StringComparison.Ordinal) == true).ToArray());
    }
}

/// <summary>Text snapshots beside this file in <c>Snapshots/</c>. Set NEXNET_UPDATE_SNAPSHOTS=1 to rewrite them.</summary>
internal static class ScaffoldSnapshots
{
    public static async Task Verify(string name, string actual)
    {
        var path = Path.Combine(Folder(), name + ".txt");
        actual = actual.Replace("\r\n", "\n");
        if (Environment.GetEnvironmentVariable("NEXNET_UPDATE_SNAPSHOTS") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await System.IO.File.WriteAllTextAsync(path, actual);
            return;
        }

        await Assert.That(System.IO.File.Exists(path)).IsTrue().Because($"Missing snapshot {path}. Run the tests with NEXNET_UPDATE_SNAPSHOTS=1 to create it.");
        var expected = (await System.IO.File.ReadAllTextAsync(path)).Replace("\r\n", "\n");
        await Assert.That(actual).IsEqualTo(expected).Because($"Scaffolding snapshot '{name}' changed. Review the difference; if intended, rerun with NEXNET_UPDATE_SNAPSHOTS=1.");
    }

    private static string Folder([CallerFilePath] string path = "") => Path.Combine(Path.GetDirectoryName(path)!, "Snapshots");
}
