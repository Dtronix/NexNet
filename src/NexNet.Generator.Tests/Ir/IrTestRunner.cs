using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using NexNet.Generator.Extraction;
using NexNet.Generator.Ir;
using NexNet.Generator.Models;
using NexNet.Generator.Serialization;

namespace NexNet.Generator.Tests.Ir;

/// <summary>The IR of a compilation plus the extractor data and C# output it was built beside.</summary>
internal sealed record IrRun(NexusIr Ir, ImmutableArray<NexusGenerationData> Data, Compilation Compilation);

/// <summary>
/// Builds the IR of test sources the way the generator pipeline will (G02): every [Nexus] class, every [NexusObject]
/// type and the assembly attributes each produce a fragment in the transform phase, merged at the end.
/// </summary>
internal static class IrTestRunner
{
    public static IrRun Run(params string[] sources)
    {
        var parseOptions = new CSharpParseOptions(LanguageVersion.CSharp13);
        var compilation = CSharpGeneratorRunner.CreateCompilation(sources[0])
            .AddSyntaxTrees(sources.Skip(1).Select(s => CSharpSyntaxTree.ParseText(s, parseOptions)));

        var generator = new CaptureGenerator();
        CSharpGeneratorDriver.Create(new[] { generator.AsSourceGenerator() }, parseOptions: parseOptions)
            .RunGenerators(compilation);

        if (generator.Ir is null)
            throw new InvalidOperationException("The capture generator produced no IR.");
        return new IrRun(generator.Ir, generator.Data, compilation);
    }

    /// <summary>Generated C# sources of the real generator, by hint name.</summary>
    public static Dictionary<string, string> RunCSharpGenerator(Compilation compilation)
    {
        var (_, result, _) = CSharpGeneratorRunner.RunGeneratorWithResult(compilation);
        return result.Results
            .SelectMany(r => r.GeneratedSources)
            .ToDictionary(s => s.HintName, s => s.SourceText.ToString());
    }

    /// <summary>The <c>IInvocationMethodHash.MethodHash</c> values in a generated nexus file: nexus first, then proxy.</summary>
    public static int[] EmittedMethodHashes(string code)
        => Regex.Matches(code, @"IInvocationMethodHash\.MethodHash \{ get => (-?\d+); \}")
            .Select(m => int.Parse(m.Groups[1].Value))
            .ToArray();

    private sealed record Produced(NexusGenerationData Data, IrFragment Fragment);

    private sealed class CaptureGenerator : IIncrementalGenerator
    {
        public NexusIr? Ir { get; private set; }

        public ImmutableArray<NexusGenerationData> Data { get; private set; } = ImmutableArray<NexusGenerationData>.Empty;

        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            var nexuses = context.SyntaxProvider.ForAttributeWithMetadataName(
                    NexusGenerator.NexusAttributeFullName,
                    static (node, _) => node is ClassDeclarationSyntax,
                    static (ctx, ct) =>
                    {
                        var data = NexusDataExtractor.Extract(ctx, ct);
                        return data == null
                            ? null
                            : new Produced(data, IrBuilder.Build(ctx.SemanticModel.Compilation, (INamedTypeSymbol)ctx.TargetSymbol, data, ct));
                    })
                .Where(static p => p != null)
                .Select(static (p, _) => p!);

            var objects = context.SyntaxProvider.ForAttributeWithMetadataName(
                NexusGenerator.NexusObjectAttributeFullName,
                static (node, _) => node is TypeDeclarationSyntax,
                static (ctx, ct) => ctx.TargetSymbol is INamedTypeSymbol symbol
                    ? IrBuilder.BuildTypes(new ShapeBuilder(ctx.SemanticModel.Compilation), new[] { symbol }, ct)
                    : IrFragment.Empty);

            var assembly = context.CompilationProvider.Select(static (c, ct) => IrBuilder.BuildAssemblyDeclared(c, ct));

            var all = nexuses.Collect().Combine(objects.Collect()).Combine(assembly);
            context.RegisterSourceOutput(all, (_, source) =>
            {
                var ((produced, fromObjects), fromAssembly) = source;
                Data = produced.Select(p => p.Data).ToImmutableArray();
                Ir = IrBuilder.Merge(produced.Select(p => p.Fragment).Concat(fromObjects).Append(fromAssembly));
            });
        }
    }
}

/// <summary>Snapshots stored beside this file in <c>Snapshots/</c> (JSON by default). Set NEXNET_UPDATE_SNAPSHOTS=1 to rewrite them.</summary>
internal static class IrSnapshots
{
    public static async Task Verify(string name, string actual, string extension = ".json")
    {
        var path = Path.Combine(Directory(), name + extension);
        actual = actual.Replace("\r\n", "\n");
        if (Environment.GetEnvironmentVariable("NEXNET_UPDATE_SNAPSHOTS") == "1")
        {
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, actual);
            return;
        }

        await Assert.That(File.Exists(path)).IsTrue().Because($"Missing snapshot {path}. Run the tests with NEXNET_UPDATE_SNAPSHOTS=1 to create it.");
        var expected = File.ReadAllText(path).Replace("\r\n", "\n");
        await Assert.That(actual).IsEqualTo(expected).Because($"IR snapshot '{name}' changed. Review the difference; if intended, rerun with NEXNET_UPDATE_SNAPSHOTS=1.");
    }

    private static string Directory([CallerFilePath] string path = "")
        => Path.Combine(Path.GetDirectoryName(path)!, "Snapshots");
}
