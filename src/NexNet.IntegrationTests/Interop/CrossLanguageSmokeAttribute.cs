using NexNet.Interop;

namespace NexNet.IntegrationTests.Interop;

/// <summary>
/// The client matrix of the cross-language smoke subset of bucket B (P01h): server-focused tests where a foreign
/// client's wire behavior matters. It yields the same rows as <see cref="ClientMatrixAttribute"/>: every language of
/// <c>NEXNET_TEST_LANGS</c> (only <c>csharp</c> when unset) times <see cref="Transports"/>, which default to HttpSocketTls
/// and WebSocketTls. A row a language cannot run is a visible skip (<see cref="ClientTarget.SkipReason"/>).
/// </summary>
/// <remarks>
/// The rest of bucket B stays C#-only (<c>[Transports]</c>). The attribute marks the subset, so it can be found and
/// filtered (<c>--treenode-filter "/*/*/CrossLanguageSmokeTests/*"</c>) without listing the tests.
/// </remarks>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
internal sealed class CrossLanguageSmokeAttribute : DataSourceGeneratorAttribute<ClientTarget>
{
    /// <summary>The transports; empty uses <see cref="ClientTransports.Default"/> (HttpSocketTls, WebSocketTls).</summary>
    public ClientTransport[] Transports { get; set; } = [];

    /// <summary>The capabilities the test needs.</summary>
    public ClientCapabilities Requires { get; set; }

    /// <summary>The rows, in language then transport order.</summary>
    public IReadOnlyList<ClientTarget> GetTargets()
        => new ClientMatrixAttribute { Transports = Transports, Requires = Requires }.GetTargets();

    /// <inheritdoc />
    protected override IEnumerable<Func<ClientTarget>> GenerateDataSources(DataGeneratorMetadata dataGeneratorMetadata)
    {
        foreach (var row in GetTargets())
            yield return () => row;
    }
}
