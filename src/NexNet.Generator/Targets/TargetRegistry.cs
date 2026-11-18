using Microsoft.CodeAnalysis.Diagnostics;
using NexNet.Generator.Serialization;
using NexNet.Generator.Targets.Java;
using NexNet.Generator.Targets.JavaScript;

namespace NexNet.Generator.Targets;

/// <summary>Every target language the generator knows, in bit order.</summary>
internal static class TargetRegistry
{
    /// <summary>MSBuild property listing the configured target ids (those with an output directory), ';' or ',' separated.</summary>
    public const string TargetsProperty = "build_property.NexNetTargets";

    /// <summary>Prefix of a target option: <c>build_property.NexNetTarget_&lt;id&gt;_&lt;Option&gt;</c>.</summary>
    public const string OptionPrefix = "build_property.NexNetTarget_";

    public static readonly ITargetLanguage[] All =
    {
        JavaTarget.Instance,
        JavaScriptTarget.Instance,
    };

    /// <summary>The target with the given id (case-insensitive), or null.</summary>
    public static ITargetLanguage? Find(string id)
    {
        foreach (var target in All)
        {
            if (string.Equals(target.Id, id, StringComparison.OrdinalIgnoreCase))
                return target;
        }

        return null;
    }

    /// <summary>The flags of every registered target.</summary>
    public static int KnownFlags
    {
        get
        {
            var flags = 0;
            foreach (var target in All)
                flags |= (int)target.Flag;
            return flags;
        }
    }

    /// <summary>The ids listed in <c>NexNetTargets</c>, lower case, without duplicates, in listed order.</summary>
    public static IReadOnlyList<string> ConfiguredIds(AnalyzerConfigOptions global)
    {
        if (!global.TryGetValue(TargetsProperty, out var value) || string.IsNullOrWhiteSpace(value))
            return Array.Empty<string>();

        var ids = new List<string>();
        foreach (var part in value.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var id = part.Trim().ToLowerInvariant();
            if (id.Length > 0 && !ids.Contains(id))
                ids.Add(id);
        }

        return ids;
    }

    /// <summary>Reads the configuration of every target.</summary>
    public static TargetConfiguration ParseConfiguration(AnalyzerConfigOptions global)
    {
        var unknown = ConfiguredIds(global)
            .Where(id => Find(id) == null)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();

        return new TargetConfiguration(
            new EquatableArray<TargetOptions>(All.Select(t => t.ParseOptions(global)).ToArray()),
            new EquatableArray<string>(unknown));
    }
}
