using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using NexNet.Generator.Bundle;

namespace NexNet.Generator.Tests.Bundle;

/// <summary>The generated sources, NEXNET diagnostics and driver of one generator run.</summary>
internal sealed record BundleRun(GeneratorDriver Driver, Compilation Compilation, GeneratorDriverRunResult Result, Diagnostic[] Diagnostics)
{
    /// <summary>The bundle source, or null when the run produced none.</summary>
    public string? Bundle => Result.Results.Single().GeneratedSources
        .Where(s => s.HintName == TargetBundle.HintName)
        .Select(s => s.SourceText.ToString())
        .SingleOrDefault();
}

/// <summary>Runs the real generator with MSBuild-style global options and step tracking.</summary>
internal static class BundleTestRunner
{
    public const string Source = """
        using System;
        using System.Threading.Tasks;
        using NexNet;

        namespace Demo;

        public partial interface IClientNexus { ValueTask Notify(string message); }
        public partial interface IServerNexus { ValueTask<int> Add(int a, int b); }

        [Nexus<IClientNexus, IServerNexus>(NexusType = NexusType.Client)]
        public partial class ClientNexus { }

        [Nexus<IServerNexus, IClientNexus>(NexusType = NexusType.Server, Targets = NexusTargets.Java | NexusTargets.JavaScript)]
        public partial class ServerNexus { }
        """;

    /// <summary>Both targets configured, as the MSBuild flattening writes them.</summary>
    public static Dictionary<string, string> Configured(bool designTime = false) => new()
    {
        ["build_property.NexNetTargets"] = "java,javascript",
        ["build_property.NexNetTarget_java_Package"] = "com.acme",
        ["build_property.DesignTimeBuild"] = designTime ? "true" : "",
    };

    public static BundleRun Run(Dictionary<string, string> globalOptions, string source = Source)
    {
        var parseOptions = new CSharpParseOptions(LanguageVersion.CSharp13);
        var compilation = CSharpGeneratorRunner.CreateCompilation(source);

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            new[] { new NexusGenerator().AsSourceGenerator() },
            parseOptions: parseOptions,
            optionsProvider: new OptionsProvider(globalOptions),
            driverOptions: new GeneratorDriverOptions(IncrementalGeneratorOutputKind.None, trackIncrementalGeneratorSteps: true));

        return Rerun(driver, compilation);
    }

    public static BundleRun Rerun(GeneratorDriver driver, Compilation compilation)
    {
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out _, out var diagnostics);
        var result = driver.GetRunResult();
        if (result.Results.FirstOrDefault(r => r.Exception != null).Exception is { } exception)
            throw new InvalidOperationException("The generator threw.", exception);

        return new BundleRun(driver, compilation, result, diagnostics.Where(d => d.Id.StartsWith("NEXNET", StringComparison.Ordinal)).ToArray());
    }

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
