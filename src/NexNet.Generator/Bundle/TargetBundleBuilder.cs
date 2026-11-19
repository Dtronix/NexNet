using NexNet.Generator.Extraction;
using NexNet.Generator.Ir;
using NexNet.Generator.Serialization;
using NexNet.Generator.Targets;

namespace NexNet.Generator.Bundle;

/// <summary>The inputs of the bundle step: the IR, the target configuration and its validation result.</summary>
/// <param name="Ir">The compilation's IR, or null when nothing opts in.</param>
/// <param name="Configuration">The MSBuild target configuration.</param>
/// <param name="Diagnostics">The target diagnostics of <paramref name="Ir"/> under <paramref name="Configuration"/>.</param>
internal sealed record TargetCheck(NexusIr? Ir, TargetConfiguration Configuration, EquatableArray<TargetDiagnostic> Diagnostics);

/// <summary>Decides whether a compilation gets a target bundle and builds its files.</summary>
internal static class TargetBundleBuilder
{
    /// <summary>
    /// Option (<c>NexNetTarget_&lt;id&gt;_EmitTestScaffolding</c>) that also emits <see cref="EmitMode.TestScaffolding"/>
    /// code. A target reads it only once it lists the option name (J11/K11).
    /// </summary>
    public const string TestScaffoldingOption = "EmitTestScaffolding";

    /// <summary>
    /// The bundle source of a compilation, or null when it gets none: no target is configured (D27: nothing changes
    /// without a <c>NexNetTarget</c>), the build is a design-time build, or a target error blocks emission.
    /// </summary>
    /// <remarks>
    /// With a target configured, a bundle is emitted even when nothing opts in (it is then empty), so the unpack
    /// target deletes files of exports that were removed.
    /// </remarks>
    public static string? Build(TargetCheck check, bool designTimeBuild)
    {
        if (designTimeBuild)
            return null;

        var files = Files(check);
        return files == null ? null : TargetBundle.Write(files);
    }

    /// <summary>The files of the bundle (see <see cref="Build"/>), or null when there is no bundle.</summary>
    public static IReadOnlyList<GeneratedFile>? Files(TargetCheck check)
    {
        if (!check.Configuration.Targets.Any(t => t.IsConfigured))
            return null;

        foreach (var diagnostic in check.Diagnostics)
        {
            if (DiagnosticDescriptors.FindTargetDescriptor(diagnostic.Id)?.DefaultSeverity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
                return null;
        }

        var files = new List<GeneratedFile>();
        var ir = check.Ir;
        if (ir == null)
            return files;

        foreach (var language in TargetRegistry.All)
        {
            var options = check.Configuration.For(language);
            if (!options.IsConfigured || !ir.Exports.Any(e => (e.Targets & (int)language.Flag) != 0))
                continue;

            var emitted = language.Emit(ir, options, EmitMode.Client);
            if (string.Equals(options.Get(TestScaffoldingOption), "true", StringComparison.OrdinalIgnoreCase))
                emitted = emitted.Concat(language.Emit(ir, options, EmitMode.TestScaffolding));

            // D31: the client runtime, beside the generated code (docs/internals/generator-emitted-runtime.md).
            emitted = emitted.Concat(language.EmitRuntime(options));

            files.AddRange(emitted
                .Select(f => new GeneratedFile(language.Id + "/" + f.Path, f.Content))
                .OrderBy(f => f.Path, StringComparer.Ordinal));
        }

        files.Add(new GeneratedFile(TargetBundle.IrFileName, IrJson.Write(ir)));
        return files;
    }
}
