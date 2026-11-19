using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using NexNet.Generator.Ir;
using NexNet.Generator.Serialization;
using NexNet.Generator.Targets;
using NexNet.Generator.Targets.Java;

namespace NexNet.Generator.Tests.Targets.Java;

/// <summary>One generator run with the Java target configured, and the Java files it emits.</summary>
internal sealed record JavaRun(Compilation Output, NexusIr? Ir, Diagnostic[] Diagnostics, Diagnostic[] CompileErrors, IReadOnlyDictionary<string, string> Files)
{
    public Diagnostic[] WithId(string id) => Diagnostics.Where(d => d.Id == id).ToArray();

    public string File(string path)
        => Files.TryGetValue(path, out var content)
            ? content
            : throw new InvalidOperationException("No file " + path + ". Emitted: " + string.Join(", ", Files.Keys));

    /// <summary>All files in one text, path-ordered, each after a <c>// ==== path</c> line (for snapshots).</summary>
    public string Concatenated()
        => string.Concat(Files.OrderBy(f => f.Key, StringComparer.Ordinal).Select(f => "// ==== " + f.Key + "\n" + f.Value));

    /// <summary>Emits the C# compilation and loads it (a fresh assembly name each time).</summary>
    public System.Reflection.Assembly Load()
    {
        using var stream = new MemoryStream();
        var result = Output.WithAssemblyName("javatest_" + Guid.NewGuid().ToString("N")).Emit(stream);
        if (!result.Success)
            throw new InvalidOperationException("Emit failed: " + string.Join("\n", result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        return System.Reflection.Assembly.Load(stream.ToArray());
    }
}

/// <summary>Runs the real generator with the Java target configured and emits the Java client code from its IR.</summary>
internal static class JavaTestRunner
{
    public static Dictionary<string, string> GlobalOptions(string package) => new()
    {
        ["build_property.NexNetTargets"] = "java",
        ["build_property.NexNetTarget_java_Package"] = package,
    };

    public static TargetOptions TargetOptionsFor(string package)
        => new("java", true, new EquatableArray<TargetOption>(new[] { new TargetOption(JavaTarget.PackageOption, package) }));

    public static JavaRun Run(string package, params string[] sources)
    {
        var parseOptions = new CSharpParseOptions(LanguageVersion.CSharp13);
        var compilation = CSharpGeneratorRunner.CreateCompilation(sources[0]);
        compilation = compilation.ReplaceSyntaxTree(compilation.SyntaxTrees.Single(), compilation.SyntaxTrees.Single().WithFilePath("Source0.cs"));
        compilation = compilation.AddSyntaxTrees(sources.Skip(1).Select((s, i) => CSharpSyntaxTree.ParseText(s, parseOptions, path: $"Source{i + 1}.cs")));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            new[] { new NexusGenerator().AsSourceGenerator() },
            parseOptions: parseOptions,
            optionsProvider: new OptionsProvider(GlobalOptions(package)),
            driverOptions: new GeneratorDriverOptions(IncrementalGeneratorOutputKind.None, trackIncrementalGeneratorSteps: true));

        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out var diagnostics);
        var result = driver.GetRunResult();
        if (result.Results.FirstOrDefault(r => r.Exception != null).Exception is { } exception)
            throw new InvalidOperationException("The generator threw.", exception);

        var ir = result.Results.Single().TrackedSteps.TryGetValue(NexusGenerator.IrTrackingName, out var step)
            ? (NexusIr?)step.Single().Outputs.Single().Value
            : null;

        var files = ir == null
            ? new Dictionary<string, string>()
            : JavaTarget.Instance.Emit(ir, TargetOptionsFor(package), EmitMode.Client).ToDictionary(f => f.Path, f => f.Content, StringComparer.Ordinal);

        return new JavaRun(
            output,
            ir,
            diagnostics.Where(d => d.Id.StartsWith("NEXNET", StringComparison.Ordinal)).ToArray(),
            output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToArray(),
            files);
    }

    /// <summary>The repository root, from this file's path.</summary>
    public static string RepoRoot([CallerFilePath] string path = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "..", "..", "..", ".."));

    private sealed class OptionsProvider : AnalyzerConfigOptionsProvider
    {
        public OptionsProvider(Dictionary<string, string> global) => GlobalOptions = new Options(global);

        public override AnalyzerConfigOptions GlobalOptions { get; }

        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => Options.Empty;

        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => Options.Empty;
    }

    private sealed class Options : AnalyzerConfigOptions
    {
        public static readonly Options Empty = new(new Dictionary<string, string>());

        private readonly Dictionary<string, string> _values;

        public Options(Dictionary<string, string> values) => _values = new Dictionary<string, string>(values, KeyComparer);

        public override bool TryGetValue(string key, [NotNullWhen(true)] out string? value) => _values.TryGetValue(key, out value);
    }
}

/// <summary>Snapshots beside this file in <c>Snapshots/</c>. Set NEXNET_UPDATE_SNAPSHOTS=1 to rewrite them.</summary>
internal static class JavaSnapshots
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
        await Assert.That(actual).IsEqualTo(expected).Because($"Java snapshot '{name}' changed. Review the difference; if intended, rerun with NEXNET_UPDATE_SNAPSHOTS=1.");
    }

    private static string Folder([CallerFilePath] string path = "") => Path.Combine(Path.GetDirectoryName(path)!, "Snapshots");
}
