using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using NexNet.Generator.Emission;
using NexNet.Generator.Extraction;
using NexNet.Generator.Ir;
using NexNet.Generator.Models;
using NexNet.Generator.Serialization;
using NexNet.Generator.Validation;

namespace NexNet.Generator;

[Generator(LanguageNames.CSharp)]
internal partial class NexusGenerator : IIncrementalGenerator
{
    public const string NexusAttributeFullName = "NexNet.NexusAttribute`2";
    public const string NexusObjectAttributeFullName = "NexNet.Serialization.NexusObjectAttribute";

    /// <summary>
    /// Hint name of the single per-assembly file holding every generated formatter and their registration.
    /// </summary>
    public const string FormattersFileName = "NexNet.Formatters.g.cs";

    /// <summary>
    /// Incremental step name of the merged formatter specs (used by tests to verify caching).
    /// </summary>
    public const string FormattersTrackingName = "NexusFormatters";

    /// <summary>
    /// Incremental step name of the compilation's IR (<see cref="NexusIr"/>, null without targets). Used by tests.
    /// </summary>
    public const string IrTrackingName = "NexusIr";

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // TRANSFORM PHASE: Extract all data here - this is cached by the incremental pipeline
        // The transform runs during semantic analysis and extracts all data into equatable records.
        // When the extracted data hasn't changed, the output phase is skipped entirely.
        // The transform also reads the multi-language opt-in (Targets) and, for an opted-in server nexus, builds its
        // IR export; without targets that is one attribute lookup.
        var nexusProducts = context.SyntaxProvider.ForAttributeWithMetadataName(
            NexusAttributeFullName,
            predicate: static (node, _) => node is ClassDeclarationSyntax,
            transform: static (ctx, ct) =>
            {
                var data = NexusDataExtractor.Extract(ctx, ct);
                return data == null ? null : new NexusProduct(data, TargetExtractor.ForNexusClass(ctx, data, ct));
            })
            .Where(static product => product is not null)
            .Select(static (product, _) => product!);

        IncrementalValuesProvider<NexusGenerationData?> nexusData = nexusProducts.Select(static (product, _) => (NexusGenerationData?)product.Data);

        var parseOptions = context.ParseOptionsProvider.Select(static (opts, _) =>
            ((CSharpParseOptions)opts).LanguageVersion);

        // Combine with language version only (NOT full compilation)
        // This is the key change - we no longer depend on CompilationProvider
        // which changes on every keystroke in any file.
        var source = nexusData.Combine(parseOptions);

        // OUTPUT PHASE: Use only extracted data - no semantic model access
        // This phase only runs when the extracted data actually changes.
        context.RegisterSourceOutput(source, static (ctx, source) =>
        {
            var (data, langVersion) = source;
            Generate(data!, langVersion, ctx);
        });

        // Formatters for [NexusObject] types declared in this compilation, so types used only at runtime
        // (e.g. CreateChannel<T>()) are registered even when no nexus references them.
        var nexusObjects = context.SyntaxProvider.ForAttributeWithMetadataName(
            NexusObjectAttributeFullName,
            predicate: static (node, _) => node is TypeDeclarationSyntax,
            transform: static (ctx, _) => ExtractNexusObject(ctx))
            .Where(static data => data is not null)
            .Select(static (data, _) => data!);

        context.RegisterSourceOutput(nexusObjects, static (ctx, data) =>
            ReportSerializationDiagnostics(data.Diagnostics, ctx));

        // Assembly level [NexusSerializable<T>] and [NexusFormatter<TFormatter, T>] declarations.
        var assemblyDeclarations = context.CompilationProvider
            .Select(static (compilation, _) =>
            {
                var builder = new SerializationBuilder(compilation, new ShapeBuilder(compilation));
                builder.AddAssemblyDeclaredRoots();
                return new SerializationOutput(builder.Build(), new EquatableArray<SerializationDiagnostic>(builder.Diagnostics.ToArray()));
            });

        context.RegisterSourceOutput(assemblyDeclarations, static (ctx, data) =>
            ReportSerializationDiagnostics(data.Diagnostics, ctx));

        // Every producer contributes formatter specs; they are merged into one file so each type gets exactly one
        // formatter per assembly. A nexus whose serialization diagnostics block generation contributes nothing.
        var nexusFormatters = nexusData.Select(static (data, _) =>
            data!.SerializationDiagnostics.Any(d => d.Id != "NEXNET037")
                ? EquatableArray<FormatterSpec>.Empty
                : data.Formatters);
        var objectFormatters = nexusObjects.Select(static (data, _) => data.Formatters);
        var assemblyFormatters = assemblyDeclarations.Select(static (data, _) => data.Formatters);

        var allFormatters = nexusFormatters.Collect()
            .Combine(objectFormatters.Collect())
            .Combine(assemblyFormatters)
            .Select(static (source, _) =>
            {
                var ((fromNexuses, fromObjects), fromAssembly) = source;
                return SerializationBuilder.Merge(fromNexuses.SelectMany(s => s)
                    .Concat(fromObjects.SelectMany(s => s))
                    .Concat(fromAssembly));
            })
            .WithTrackingName(FormattersTrackingName);

        context.RegisterSourceOutput(allFormatters, static (ctx, specs) =>
        {
            var code = SerializationBuilder.EmitSource(specs);
            if (code.Length > 0)
                ctx.AddSource(FormattersFileName, code);
        });

        var ir = CreateIrPipeline(context, nexusProducts);

        // Target emitters (G03+) consume the IR here. Nothing is emitted yet; the output keeps the step reachable.
        context.RegisterSourceOutput(ir, static (_, _) => { });
    }

    /// <summary>
    /// The multi-language opt-in pipeline: reports NEXNET043/NEXNET048 and produces the compilation's
    /// <see cref="NexusIr"/>, or null when no nexus opts in (then nothing beyond reading attributes runs).
    /// </summary>
    private static IncrementalValueProvider<NexusIr?> CreateIrPipeline(
        IncrementalGeneratorInitializationContext context,
        IncrementalValuesProvider<NexusProduct> nexusProducts)
    {
        var classTargets = nexusProducts
            .Where(static product => product.Targets is not null)
            .Select(static (product, _) => product.Targets!);

        context.RegisterSourceOutput(classTargets.Where(static t => t.IsClient), static (ctx, targets) =>
            ctx.ReportDiagnostic(Diagnostic.Create(
                DiagnosticDescriptors.TargetsOnClientNexus,
                NexusValidator.ToDiagnosticLocation(targets.Location),
                targets.TypeName)));

        var assemblyTargets = context.CompilationProvider
            .Select(static (compilation, ct) => TargetExtractor.ExtractAssembly(compilation, ct));

        context.RegisterSourceOutput(assemblyTargets, static (ctx, data) =>
        {
            foreach (var diag in data.Diagnostics)
            {
                var descriptor = DiagnosticDescriptors.FindTargetDescriptor(diag.Id);
                if (descriptor != null)
                    ctx.ReportDiagnostic(Diagnostic.Create(descriptor, NexusValidator.ToDiagnosticLocation(diag.Location), diag.Arg0, diag.Arg1));
            }
        });

        // [NexusObject] types are part of the IR (types used only at runtime need formatters in every language).
        // Only their ids flow here; they are resolved when an IR is built.
        var nexusObjectIds = context.SyntaxProvider.ForAttributeWithMetadataName(
                NexusObjectAttributeFullName,
                predicate: static (node, _) => node is TypeDeclarationSyntax,
                transform: static (ctx, _) => ctx.TargetSymbol is INamedTypeSymbol symbol
                    ? SerializationBuilder.MetadataFullName(symbol.OriginalDefinition)
                    : null)
            .Where(static id => id is not null)
            .Select(static (id, _) => id!);

        return classTargets.Collect()
            .Combine(assemblyTargets)
            .Combine(nexusObjectIds.Collect())
            .Combine(context.CompilationProvider)
            .Select(static (source, ct) =>
            {
                var (((fromClasses, assembly), objectIds), compilation) = source;
                return TargetExtractor.BuildIr(fromClasses, assembly, objectIds, compilation, ct);
            })
            .WithTrackingName(IrTrackingName);
    }

    private sealed record SerializationOutput(EquatableArray<FormatterSpec> Formatters, EquatableArray<SerializationDiagnostic> Diagnostics);

    private static SerializationOutput? ExtractNexusObject(GeneratorAttributeSyntaxContext ctx)
    {
        if (ctx.TargetSymbol is not INamedTypeSymbol symbol || symbol.IsGenericType)
            return null;

        var compilation = ctx.SemanticModel.Compilation;
        var builder = new SerializationBuilder(compilation, new ShapeBuilder(compilation));
        builder.Require(symbol, symbol.Name, LocationData.FromSymbol(symbol));
        return new SerializationOutput(builder.Build(), new EquatableArray<SerializationDiagnostic>(builder.Diagnostics.ToArray()));
    }

    private static void ReportSerializationDiagnostics(IEnumerable<SerializationDiagnostic> diagnostics, SourceProductionContext context)
    {
        foreach (var diag in diagnostics)
        {
            var descriptor = DiagnosticDescriptors.FindSerializationDescriptor(diag.Id);
            if (descriptor != null)
                context.ReportDiagnostic(Diagnostic.Create(descriptor, NexusValidator.ToDiagnosticLocation(diag.Location), diag.Arg0, diag.Arg1));
        }
    }

    private static void Generate(
        NexusGenerationData data,
        LanguageVersion langVersion,
        SourceProductionContext context)
    {
        // Validate using cached data (no ISymbol references)
        var diagnostics = NexusValidator.Validate(data, context.CancellationToken);

        bool hasBlockingErrors = false;
        foreach (var diagnostic in diagnostics)
        {
            context.ReportDiagnostic(diagnostic);
            // HashLock mismatch is an error but shouldn't block generation
            if (diagnostic.Severity == DiagnosticSeverity.Error &&
                diagnostic.Id != DiagnosticDescriptors.VersionHashLockMismatch.Id)
            {
                hasBlockingErrors = true;
            }
        }

        ReportSerializationDiagnostics(data.SerializationDiagnostics, context);
        if (data.SerializationDiagnostics.Any(d => d.Id != "NEXNET037"))
            hasBlockingErrors = true;

        if (hasBlockingErrors)
            return;

        // Generate code using cached data
        var sb = SymbolUtilities.GetStringBuilder();

        sb.AppendLine(@"// <auto-generated/>
#nullable enable
");

        var code = NexusEmitter.Emit(data, langVersion);
        sb.Append(code);

        var finalCode = sb.ToString();
        SymbolUtilities.ReturnStringBuilder(sb);

        context.AddSource($"{data.FullTypeName}.Nexus.g.cs", finalCode);
    }
}
