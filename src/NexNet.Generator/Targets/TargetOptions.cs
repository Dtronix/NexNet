using NexNet.Generator.Serialization;

namespace NexNet.Generator.Targets;

/// <summary>One <c>NexNetTarget_&lt;id&gt;_&lt;Name&gt;</c> option value.</summary>
internal sealed record TargetOption(string Name, string Value);

/// <summary>The configuration of one target, read from the global analyzer options.</summary>
/// <param name="TargetId">The target id.</param>
/// <param name="IsConfigured">True when <c>NexNetTargets</c> lists the target, i.e. it has an output directory.</param>
/// <param name="Values">The target's option values that are set and not blank, in the target's option order.</param>
internal sealed record TargetOptions(string TargetId, bool IsConfigured, EquatableArray<TargetOption> Values)
{
    /// <summary>The trimmed value of an option, or null when it is not set.</summary>
    public string? Get(string name)
    {
        foreach (var value in Values)
        {
            if (string.Equals(value.Name, name, StringComparison.OrdinalIgnoreCase))
                return value.Value;
        }

        return null;
    }
}

/// <summary>The target configuration of a compilation.</summary>
/// <param name="Targets">The options of every registered target, in registry order.</param>
/// <param name="UnknownTargets">Ids listed in <c>NexNetTargets</c> that no registered target has, lower case, ordinal order.</param>
internal sealed record TargetConfiguration(EquatableArray<TargetOptions> Targets, EquatableArray<string> UnknownTargets)
{
    public TargetOptions For(ITargetLanguage language)
    {
        foreach (var options in Targets)
        {
            if (options.TargetId == language.Id)
                return options;
        }

        return new TargetOptions(language.Id, false, EquatableArray<TargetOption>.Empty);
    }
}
