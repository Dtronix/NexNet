using Microsoft.CodeAnalysis.Diagnostics;
using NexNet.Generator.Extraction;
using NexNet.Generator.Ir;

namespace NexNet.Generator.Targets;

/// <summary>
/// Generator-side mirror of <c>NexNet.NexusTargets</c> (the generator does not reference the runtime). Bit values are
/// the same and never change: they are stored in the IR.
/// </summary>
[Flags]
internal enum NexusTargets
{
    None = 0,
    Java = 1,
    JavaScript = 2,
}

/// <summary>What <see cref="ITargetLanguage.Emit"/> produces.</summary>
internal enum EmitMode : byte
{
    /// <summary>The client library code: proxies, client nexus bases, formatters.</summary>
    Client,

    /// <summary>The scriptable test-host code used by the cross-language test harness.</summary>
    TestScaffolding,
}

/// <summary>
/// One client language the generator produces code for. Implementations are stateless singletons registered in
/// <see cref="TargetRegistry"/>. Everything here works on the IR only (no Roslyn symbols) and is deterministic.
/// </summary>
internal interface ITargetLanguage
{
    /// <summary>The target id used in MSBuild configuration: <c>java</c>, <c>javascript</c>.</summary>
    string Id { get; }

    /// <summary>Display name used in diagnostics: <c>Java</c>, <c>JavaScript</c>.</summary>
    string DisplayName { get; }

    /// <summary>The <c>NexusTargets</c> bit that opts an export in to this target.</summary>
    NexusTargets Flag { get; }

    /// <summary>
    /// Reads this target's configuration from the global analyzer options: whether <c>build_property.NexNetTargets</c>
    /// lists it (it has an output directory) and its <c>build_property.NexNetTarget_&lt;id&gt;_&lt;Option&gt;</c> values.
    /// </summary>
    TargetOptions ParseOptions(AnalyzerConfigOptions global);

    /// <summary>Problems with a configured target's options (NEXNET045), e.g. a missing Java package.</summary>
    IEnumerable<string> ValidateOptions(TargetOptions options);

    /// <summary>Maps IR type references to this language (drives NEXNET040 and NEXNET046).</summary>
    ITypeMapper Types { get; }

    /// <summary>Maps .NET names to this language (drives NEXNET041).</summary>
    INameMapper Names { get; }

    /// <summary>
    /// Language checks of one export opted in to this target: unrepresentable types (NEXNET040), name collisions
    /// (NEXNET041), duplicate method names (NEXNET042) and reference-equality keys (NEXNET046). Declared types are
    /// checked with the first export (in IR order) that reaches them, so each is reported once.
    /// </summary>
    IEnumerable<TargetDiagnostic> Validate(NexusIr ir, ExportIr export, TargetOptions options);

    /// <summary>
    /// Paths, relative to the target's output directory and separated by '/', of the files <see cref="Emit"/> writes
    /// for an export. Used to find output collisions (NEXNET047) before anything is emitted.
    /// </summary>
    IEnumerable<string> ExportFiles(ExportIr export, TargetOptions options);

    /// <summary>As <see cref="ExportFiles"/>, for a declared type.</summary>
    IEnumerable<string> TypeFiles(TypeDeclIr type, TargetOptions options);

    /// <summary>Emits the files of this target for the whole IR. Paths follow <see cref="ExportFiles"/>/<see cref="TypeFiles"/>.</summary>
    IEnumerable<GeneratedFile> Emit(NexusIr ir, TargetOptions options, EmitMode mode);
}
