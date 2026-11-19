using System.Collections.Concurrent;
using TUnit.Core.Interfaces;

namespace NexNet.Interop.Hosts;

/// <summary>
/// Limits how many tests run at once on each language host (control protocol §9): a test whose <see cref="ClientTarget"/>
/// argument names a remote language shares that language's limit (<c>maxConcurrency</c> of its <c>host.json</c>,
/// default 4). C# rows are not limited.
/// </summary>
/// <example>
/// <code>
/// [Test, ClientMatrix, LanguageParallelLimit]
/// public async Task Echo(ClientTarget target) { … }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class | AttributeTargets.Assembly, AllowMultiple = false)]
public sealed class LanguageParallelLimitAttribute : Attribute, ITestRegisteredEventReceiver
{
    /// <inheritdoc />
    public int Order => 0;

    /// <inheritdoc />
    public ValueTask OnTestRegistered(TestRegisteredContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var target = context.TestDetails.TestMethodArguments.OfType<ClientTarget>().FirstOrDefault()
                     ?? context.TestDetails.TestClassArguments.OfType<ClientTarget>().FirstOrDefault();
        if (target != null && target.Language != ClientLanguages.CSharp)
            context.SetParallelLimiter(LanguageParallelLimit.For(target.Language));
        return default;
    }
}

/// <summary>The parallel limit of one language.</summary>
public sealed class LanguageParallelLimit : IParallelLimit
{
    private static readonly ConcurrentDictionary<string, LanguageParallelLimit> Limits = new(StringComparer.Ordinal);

    private LanguageParallelLimit(string language, int limit)
    {
        Language = language;
        Limit = limit;
    }

    /// <summary>The language.</summary>
    public string Language { get; }

    /// <inheritdoc />
    public int Limit { get; }

    /// <summary>The shared limit of <paramref name="language"/>: <c>maxConcurrency</c> of its host.json, or the default.</summary>
    public static LanguageParallelLimit For(string language)
    {
        ArgumentNullException.ThrowIfNull(language);
        return Limits.GetOrAdd(language, static l =>
        {
            int limit;
            try
            {
                limit = HostLaunchConfig.Load(l).MaxConcurrency;
            }
            catch (Exception)
            {
                limit = HostLaunchConfig.DefaultMaxConcurrency;
            }

            return new LanguageParallelLimit(l, limit);
        });
    }
}
