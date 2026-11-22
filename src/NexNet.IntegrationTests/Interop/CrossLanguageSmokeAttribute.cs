using NexNet.Interop;
using TUnit.Core.Interfaces;

namespace NexNet.IntegrationTests.Interop;

/// <summary>
/// The client matrix of the cross-language smoke subset of bucket B (P01h): server-focused tests where a foreign
/// client's wire behavior matters. It yields the same rows as <see cref="ClientMatrixAttribute"/>: every language of
/// <c>NEXNET_TEST_LANGS</c> (only <c>csharp</c> when unset) times <see cref="Transports"/>, which default to HttpSocketTls
/// and WebSocketTls. Only applicable rows are generated (a language that lacks a transport or capability gets no row for
/// it), and a language's variants add their rows (<see cref="ClientTarget.Variant"/>).
/// </summary>
/// <remarks>
/// The rest of bucket B stays C#-only (<c>[Transports]</c>). The attribute marks the subset, so it can be found and
/// filtered (<c>--treenode-filter "/*/*/CrossLanguageSmokeTests/*"</c>) without listing the tests. Like
/// <see cref="ClientMatrixAttribute"/>, every row carries the <c>Lang</c> and <c>Transport</c> properties, so
/// <c>--treenode-filter "/*/*/CrossLanguageSmokeTests/*[Lang=node]"</c> selects one language's rows, and its rows are in the
/// coverage report (<see cref="MatrixCoverage"/>).
/// </remarks>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
internal sealed class CrossLanguageSmokeAttribute : DataSourceGeneratorAttribute<ClientTarget>, ITestDiscoveryEventReceiver,
    ITestEndEventReceiver, ITestSkippedEventReceiver
{
    /// <summary>The transports; empty uses <see cref="ClientTransports.Default"/> (HttpSocketTls, WebSocketTls).</summary>
    public ClientTransport[] Transports { get; set; } = [];

    /// <summary>The capabilities the test needs.</summary>
    public ClientCapabilities Requires { get; set; }

    /// <summary>The settings of this attribute as a <see cref="ClientMatrixAttribute"/>, which builds the rows.</summary>
    private ClientMatrixAttribute Matrix => new() { Transports = Transports, Requires = Requires };

    /// <summary>The rows, in language then transport order.</summary>
    public IReadOnlyList<ClientTarget> GetTargets() => Matrix.GetTargets();

    /// <inheritdoc />
    protected override IEnumerable<Func<ClientTarget>> GenerateDataSources(DataGeneratorMetadata dataGeneratorMetadata)
    {
        foreach (var row in Matrix.PlanAndRecord(dataGeneratorMetadata).Rows)
            yield return () => row;
    }

    /// <summary>Tags the discovered row with its <c>Lang</c>, <c>Transport</c> and <c>Variant</c> properties.</summary>
    /// <param name="context">The discovered test.</param>
    public ValueTask OnTestDiscovered(DiscoveredTestContext context) => ClientMatrixAttribute.TagDiscoveredRow(context);

    /// <inheritdoc cref="ClientMatrixAttribute.OnTestEnd"/>
    public ValueTask OnTestEnd(TestContext context) => MatrixCoverage.RecordResult(context, skipped: false);

    /// <inheritdoc cref="ClientMatrixAttribute.OnTestSkipped"/>
    public ValueTask OnTestSkipped(TestContext context) => MatrixCoverage.RecordResult(context, skipped: true);
}
