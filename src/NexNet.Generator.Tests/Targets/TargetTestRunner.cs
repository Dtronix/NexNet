using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using NexNet.Generator.Ir;

namespace NexNet.Generator.Tests.Targets;

/// <summary>The NEXNET diagnostics and IR of one generator run.</summary>
internal sealed record TargetRun(NexusIr? Ir, Diagnostic[] Diagnostics)
{
    public Diagnostic[] WithId(string id) => Diagnostics.Where(d => d.Id == id).ToArray();

    public string[] Messages(string id) => WithId(id).Select(d => d.GetMessage(System.Globalization.CultureInfo.InvariantCulture)).ToArray();
}

/// <summary>Runs the real generator with MSBuild-style global options (<c>build_property.*</c>).</summary>
internal static class TargetTestRunner
{
    public const string Usings = """
        using System;
        using System.Collections.Generic;
        using System.Threading.Tasks;
        using NexNet;
        using NexNet.Serialization;

        """;

    /// <summary>Global options of a project configuring Java (package com.acme) and JavaScript.</summary>
    public static Dictionary<string, string> BothConfigured() => new()
    {
        ["build_property.NexNetTargets"] = "java;javascript",
        ["build_property.NexNetTarget_java_Package"] = "com.acme",
    };

    public static TargetRun Run(Dictionary<string, string>? globalOptions, params string[] sources)
    {
        var parseOptions = new CSharpParseOptions(LanguageVersion.CSharp13);
        var compilation = CSharpGeneratorRunner.CreateCompilation(sources[0]);
        compilation = compilation.ReplaceSyntaxTree(compilation.SyntaxTrees.Single(), compilation.SyntaxTrees.Single().WithFilePath("Source0.cs"));
        compilation = compilation.AddSyntaxTrees(sources.Skip(1).Select((s, i) => CSharpSyntaxTree.ParseText(s, parseOptions, path: $"Source{i + 1}.cs")));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            new[] { new NexusGenerator().AsSourceGenerator() },
            parseOptions: parseOptions,
            optionsProvider: new TestOptionsProvider(globalOptions ?? new Dictionary<string, string>()),
            driverOptions: new GeneratorDriverOptions(IncrementalGeneratorOutputKind.None, trackIncrementalGeneratorSteps: true));

        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out _, out var diagnostics);
        var result = driver.GetRunResult();
        if (result.Results.FirstOrDefault(r => r.Exception != null).Exception is { } exception)
            throw new InvalidOperationException("The generator threw.", exception);

        var steps = result.Results.Single().TrackedSteps;
        var ir = steps.TryGetValue(NexusGenerator.IrTrackingName, out var step)
            ? (NexusIr?)step.Single().Outputs.Single().Value
            : null;

        return new TargetRun(ir, diagnostics.Where(d => d.Id.StartsWith("NEXNET", StringComparison.Ordinal)).ToArray());
    }

    internal sealed class TestOptionsProvider : AnalyzerConfigOptionsProvider
    {
        public TestOptionsProvider(Dictionary<string, string> global) => GlobalOptions = new Options(global);

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
