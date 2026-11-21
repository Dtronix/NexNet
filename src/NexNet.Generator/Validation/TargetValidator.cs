using System.Globalization;
using NexNet.Generator.Extraction;
using NexNet.Generator.Ir;
using NexNet.Generator.Models;
using NexNet.Generator.Serialization;
using NexNet.Generator.Targets;

namespace NexNet.Generator.Validation;

/// <summary>
/// Validates the compilation's IR against every target language it opts in to, in the output phase (no symbols, only
/// IR and configuration). Reports NEXNET040, 041, 042, 044, 045, 046 and 047; the language-specific checks are
/// <see cref="ITargetLanguage.Validate"/>.
/// </summary>
internal static class TargetValidator
{
    /// <summary>
    /// All target diagnostics of a compilation, in a deterministic order.
    /// </summary>
    /// <param name="ir">The compilation's IR; null when nothing opts in.</param>
    /// <param name="configuration">The MSBuild target configuration.</param>
    public static List<TargetDiagnostic> Validate(NexusIr? ir, TargetConfiguration configuration)
    {
        var diagnostics = new List<TargetDiagnostic>();

        // NEXNET045: the configuration is wrong whether or not anything opts in.
        foreach (var unknown in configuration.UnknownTargets)
        {
            diagnostics.Add(new TargetDiagnostic(
                DiagnosticDescriptors.InvalidTargetConfiguration.Id,
                "unknown target '" + unknown + "' in NexNetTargets (known: " + string.Join(", ", TargetRegistry.All.Select(t => t.Id)) + ")",
                "",
                null));
        }

        var valid = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var language in TargetRegistry.All)
        {
            var options = configuration.For(language);
            var ok = true;
            if (options.IsConfigured)
            {
                foreach (var problem in language.ValidateOptions(options))
                {
                    ok = false;
                    diagnostics.Add(new TargetDiagnostic(DiagnosticDescriptors.InvalidTargetConfiguration.Id, problem, "", null));
                }
            }

            valid[language.Id] = ok;
        }

        if (ir == null)
            return diagnostics;

        var known = TargetRegistry.KnownFlags;
        foreach (var export in ir.Exports)
        {
            var unknownBits = export.Targets & ~known;
            if (unknownBits != 0)
            {
                diagnostics.Add(new TargetDiagnostic(
                    DiagnosticDescriptors.InvalidTargetConfiguration.Id,
                    "'" + ExportName(export) + "' requests the unknown NexusTargets value " + unknownBits.ToString(CultureInfo.InvariantCulture),
                    "",
                    export.Location));
            }
        }

        foreach (var language in TargetRegistry.All)
        {
            var exports = ir.Exports.Where(e => (e.Targets & (int)language.Flag) != 0).ToArray();
            if (exports.Length == 0)
                continue;

            var options = configuration.For(language);
            if (!options.IsConfigured)
            {
                // NEXNET044: nothing is generated for the target, so nothing else about it is checked.
                foreach (var export in exports)
                {
                    diagnostics.Add(new TargetDiagnostic(
                        DiagnosticDescriptors.TargetWithoutOutput.Id, ExportName(export), language.DisplayName, export.Location));
                }

                continue;
            }

            foreach (var export in exports)
                diagnostics.AddRange(language.Validate(ir, export, options));

            if (valid[language.Id])
                diagnostics.AddRange(FileCollisions(ir, language, options));
        }

        return diagnostics;
    }

    /// <summary>
    /// NEXNET041 for exports declared more than once with the same namespace and name (e.g. a <c>[Nexus(Targets)]</c>
    /// class and a <c>NexusClientExport</c>): the IR keeps only one of them. One diagnostic per declaration.
    /// </summary>
    public static EquatableArray<TargetDiagnostic> DuplicateExports(IEnumerable<ExportIr> exports)
    {
        var result = new List<TargetDiagnostic>();
        foreach (var group in exports
                     .GroupBy(e => (e.ClrNamespace, e.Name))
                     .Where(g => g.Count() > 1)
                     .OrderBy(g => g.Key.ClrNamespace, StringComparer.Ordinal)
                     .ThenBy(g => g.Key.Name, StringComparer.Ordinal))
        {
            var name = ExportName(group.First());
            foreach (var export in group.OrderBy(e => e.Location?.FilePath, StringComparer.Ordinal).ThenBy(e => e.Location?.StartPosition))
            {
                result.Add(new TargetDiagnostic(
                    DiagnosticDescriptors.TargetNameCollision.Id,
                    "exports",
                    "'" + name + "' is exported more than once (a [Nexus] class with Targets and a NexusClientExport, or several NexusClientExports, share the namespace and name); give each NexusClientExport a distinct Name",
                    export.Location));
            }
        }

        return new EquatableArray<TargetDiagnostic>(result.ToArray());
    }

    /// <summary>NEXNET047: two exports or types that would write the same file (compared ignoring case).</summary>
    private static IEnumerable<TargetDiagnostic> FileCollisions(NexusIr ir, ITargetLanguage language, TargetOptions options)
    {
        var files = new List<(string Path, string Owner, LocationData? Location)>();
        foreach (var export in ir.Exports.Where(e => (e.Targets & (int)language.Flag) != 0))
        {
            foreach (var path in language.ExportFiles(export, options))
                files.Add((path, "export '" + ExportName(export) + "'", export.Location));
        }

        foreach (var (decl, owner) in TargetIrWalker.TargetDecls(ir, language.Flag))
        {
            foreach (var path in language.TypeFiles(decl, options))
                files.Add((path, "type '" + decl.Id + "'", owner.Location));
        }

        // D31: the emitted client runtime shares the output directory.
        foreach (var file in language.EmitRuntime(options))
            files.Add((file.Path, "the " + language.DisplayName + " client runtime (EmitRuntime)", null));

        foreach (var group in files.GroupBy(f => f.Path, StringComparer.OrdinalIgnoreCase))
        {
            var owners = group.Select(f => f.Owner).Distinct(StringComparer.Ordinal).ToArray();
            if (owners.Length < 2)
                continue;

            yield return new TargetDiagnostic(
                DiagnosticDescriptors.TargetFileCollision.Id,
                language.DisplayName,
                "'" + group.First().Path + "' would be written by " + string.Join(" and ", owners),
                group.Select(f => f.Location).Skip(1).FirstOrDefault(l => l != null) ?? group.First().Location);
        }
    }

    private static string ExportName(ExportIr export)
        => export.ClrNamespace.Length == 0 ? export.Name : export.ClrNamespace + "." + export.Name;
}
